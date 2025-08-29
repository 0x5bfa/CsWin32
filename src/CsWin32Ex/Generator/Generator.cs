// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

/// <summary>
/// The core of the source generator.
/// </summary>
[DebuggerDisplay("{" + nameof(DebuggerDisplayString) + ",nq}")]
public partial class Generator : IGenerator, IDisposable
{
	private readonly TypeSyntaxSettings _generalTypeSettings;
	private readonly TypeSyntaxSettings _fieldTypeSettings;
	private readonly TypeSyntaxSettings _delegateSignatureTypeSettings;
	private readonly TypeSyntaxSettings _enumTypeSettings;
	private readonly TypeSyntaxSettings _fieldOfHandleTypeDefTypeSettings;
	private readonly TypeSyntaxSettings _externSignatureTypeSettings;
	private readonly TypeSyntaxSettings _externReleaseSignatureTypeSettings;
	private readonly TypeSyntaxSettings _comSignatureTypeSettings;
	private readonly TypeSyntaxSettings _extensionMethodSignatureTypeSettings;
	private readonly TypeSyntaxSettings _functionPointerTypeSettings;
	private readonly TypeSyntaxSettings _errorMessageTypeSettings;

	private readonly ClassDeclarationSyntax _comHelperClass;

	/// <summary>
	/// The struct with one type parameter used to represent a variable-length inline array.
	/// </summary>
	private readonly StructDeclarationSyntax _variableLengthInlineArrayStruct1;

	/// <summary>
	/// The struct with two type parameters used to represent a variable-length inline array.
	/// This is useful when the exposed type parameter is C# unmanaged but runtime unblittable (i.e. <see langword="bool" /> and <see langword="char" />).
	/// </summary>
	private readonly StructDeclarationSyntax variableLengthInlineArrayStruct2;

	private readonly Dictionary<string, IReadOnlyList<ISymbol>> _findTypeSymbolIfAlreadyAvailableCache = new(StringComparer.Ordinal);
	private readonly WinMDReaderRental _winMDReaderRental;
	private readonly GeneratorOptions _options;
	private readonly CSharpCompilation? _compilation;
	private readonly CSharpParseOptions? _parseOptions;
	private readonly bool _comIIDInterfacePredefined;
	private readonly bool _getDelegateForFunctionPointerGenericExists;
	private readonly GeneratedCode _committedCode = new();
	private readonly GeneratedCode _volatileCode;
	private readonly IdentifierNameSyntax _methodsAndConstantsClassName;
	private readonly HashSet<string> _injectedPInvokeHelperMethods = [];
	private readonly HashSet<string> _injectedPInvokeMacros = [];
	private readonly Dictionary<TypeDefinitionHandle, bool> _managedTypesCheck = [];
	private MethodDeclarationSyntax? _sliceAtNullMethodDecl;

	internal ImmutableDictionary<string, string> BannedAPIs => GetBannedAPIs(_options);

	internal GeneratorManager? Manager { get; set; }

	/// <summary>
	/// Gets the Windows.Win32 generator.
	/// </summary>
	internal Generator MainGenerator
	{
		get
		{
			if (IsWin32Sdk || Manager is null)
			{
				return this;
			}

			if (Manager.TryGetGenerator("Windows.Win32", out Generator? generator))
			{
				return generator;
			}

			throw new InvalidOperationException("Unable to find Windows.Win32 generator.");
		}
	}

	internal GeneratorOptions Options => _options;

	internal string InputAssemblyName => WinMDIndexer.WinMDAssemblyName;

	internal WinMDFileIndexer WinMDIndexer { get; }

	internal MetadataReader WinMDReader => _winMDReaderRental.Value;

	internal LanguageVersion LanguageVersion => _parseOptions?.LanguageVersion ?? LanguageVersion.CSharp9;

	/// <summary>
	/// Gets the default generation context to use.
	/// </summary>
	internal Context DefaultContext => new() { AllowMarshaling = _options.AllowMarshaling };

	private HashSet<string> AppLocalLibraries { get; }

	private bool WideCharOnly => _options.WideCharOnly;

	private string Namespace => WinMDIndexer.CommonNamespace;

	private SyntaxKind Visibility => _options.Public ? SyntaxKind.PublicKeyword : SyntaxKind.InternalKeyword;

	private bool IsWin32Sdk => string.Equals(WinMDIndexer.WinMDAssemblyName, "Windows.Win32", StringComparison.OrdinalIgnoreCase);

	private IEnumerable<MemberDeclarationSyntax> NamespaceMembers
	{
		get
		{
			IEnumerable<IGrouping<string, MemberDeclarationSyntax>> members = _committedCode.MembersByModule;
			IEnumerable<MemberDeclarationSyntax> result = Enumerable.Empty<MemberDeclarationSyntax>();
			int i = 0;
			foreach (IGrouping<string, MemberDeclarationSyntax> entry in members)
			{
				ClassDeclarationSyntax partialClass = DeclarePInvokeClass(entry.Key)
					.AddMembers(entry.ToArray())
					.WithLeadingTrivia(ParseLeadingTrivia(string.Format(CultureInfo.InvariantCulture, PartialPInvokeContentComment, entry.Key)));
				if (i == 0)
				{
					partialClass = partialClass
						.WithoutLeadingTrivia()
						.AddAttributeLists(AttributeList().AddAttributes(GeneratedCodeAttribute))
						.WithLeadingTrivia(partialClass.GetLeadingTrivia());
				}

				result = result.Concat(new MemberDeclarationSyntax[] { partialClass });
				i++;
			}

			ClassDeclarationSyntax macrosPartialClass = DeclarePInvokeClass("Macros")
				.AddMembers(_committedCode.Macros.ToArray())
				.WithLeadingTrivia(ParseLeadingTrivia(PartialPInvokeMacrosContentComment));
			if (macrosPartialClass.Members.Count > 0)
			{
				result = result.Concat(new MemberDeclarationSyntax[] { macrosPartialClass });
			}

			ClassDeclarationSyntax DeclarePInvokeClass(string fileNameKey) => ClassDeclaration(Identifier(_options.ClassName))
				.AddModifiers(TokenWithSpace(Visibility), TokenWithSpace(SyntaxKind.StaticKeyword), TokenWithSpace(SyntaxKind.PartialKeyword))
				.WithAdditionalAnnotations(new SyntaxAnnotation(SimpleFileNameAnnotation, $"{_options.ClassName}.{fileNameKey}"));

			result = result.Concat(_committedCode.GeneratedTypes);

			ClassDeclarationSyntax inlineArrayIndexerExtensionsClass = DeclareInlineArrayIndexerExtensionsClass();
			if (inlineArrayIndexerExtensionsClass.Members.Count > 0)
			{
				result = result.Concat(new MemberDeclarationSyntax[] { inlineArrayIndexerExtensionsClass });
			}

			result = result.Concat(_committedCode.ComInterfaceExtensions);

			if (_committedCode.TopLevelFields.Any())
			{
				result = result.Concat(new MemberDeclarationSyntax[] { DeclareConstantDefiningClass() });
			}

			return result;
		}
	}

	private string DebuggerDisplayString => $"Generator: {InputAssemblyName}";

	static Generator()
	{
		if (!TryFetchTemplate("PInvokeClassHelperMethods", null, out MemberDeclarationSyntax? member))
			throw new GenerationFailedException("Missing embedded resource.");

		PInvokeHelperMethods = ((ClassDeclarationSyntax)member).Members.OfType<MethodDeclarationSyntax>().ToDictionary(m => m.Identifier.ValueText, m => m);

		if (!TryFetchTemplate("PInvokeClassMacros", null, out member))
			throw new GenerationFailedException("Missing embedded resource.");

		Win32SdkMacros = ((ClassDeclarationSyntax)member).Members.OfType<MethodDeclarationSyntax>().ToDictionary(m => m.Identifier.ValueText, m => m);

		FetchTemplate("IVTable", null, out IVTableInterface);
		FetchTemplate("IVTable`2", null, out IVTableGenericInterface);
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="Generator"/> class.
	/// </summary>
	/// <param name="winMDPath">The path to the winmd metadata to generate APIs from.</param>
	/// <param name="apiDocs">The API docs to include in the generated code.</param>
	/// <param name="additionalAppLocalLibraries">The library file names (e.g. some.dll) that should be allowed as app-local.</param>
	/// <param name="options">Options that influence the result of generation.</param>
	/// <param name="compilation">The compilation that the generated code will be added to.</param>
	/// <param name="parseOptions">The parse options that will be used for the generated code.</param>
	public Generator(string winMDPath, Docs? apiDocs, IEnumerable<string> additionalAppLocalLibraries, GeneratorOptions options, CSharpCompilation? compilation = null, CSharpParseOptions? parseOptions = null)
	{
		// Initialize the WinMD reader and indexer.
		WinMDFile winMDFile = WinMDFile.Create(winMDPath);
		WinMDIndexer = winMDFile.GetWinMDIndexer(compilation?.Options.Platform);
		_winMDReaderRental = winMDFile.RentWinMDReader();

		// Initialize the API docs
		ApiDocs = apiDocs;

		// Initializes the native libraries along with the default native libraries that are allowed as app-local.
		AppLocalLibraries = new(BuiltInAppLocalLibraries, StringComparer.OrdinalIgnoreCase);
		AppLocalLibraries.UnionWith(additionalAppLocalLibraries);

		// Initialize the options
		_options = options;
		_compilation = compilation;
		_parseOptions = parseOptions;
		_volatileCode = new(_committedCode);

		_canUseUnscopedRef = _parseOptions?.LanguageVersion >= (LanguageVersion)1100; // C# 11.0
		_canUseSpan = _compilation?.GetTypeByMetadataName(typeof(Span<>).FullName) is not null;
		_canCallCreateSpan = _compilation?.GetTypeByMetadataName(typeof(MemoryMarshal).FullName)?.GetMembers("CreateSpan").Any() is true;
		_canUseUnsafeAsRef = _compilation?.GetTypeByMetadataName(typeof(Unsafe).FullName)?.GetMembers("Add").Any() is true;
		_canUseUnsafeAdd = _compilation?.GetTypeByMetadataName(typeof(Unsafe).FullName)?.GetMembers("AsRef").Any() is true;
		_canUseUnsafeNullRef = _compilation?.GetTypeByMetadataName(typeof(Unsafe).FullName)?.GetMembers("NullRef").Any() is true;
		_canUseUnsafeSkipInit = _compilation?.GetTypeByMetadataName(typeof(Unsafe).FullName)?.GetMembers("SkipInit").Any() is true;
		_canUseUnmanagedCallersOnlyAttribute = FindTypeSymbolsIfAlreadyAvailable("System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute").Count > 0;
		_canUseSetLastPInvokeError = _compilation?.GetTypeByMetadataName("System.Runtime.InteropServices.Marshal")?.GetMembers("GetLastSystemError").IsEmpty is false;
		_unscopedRefAttributePredefined = FindTypeSymbolIfAlreadyAvailable("System.Diagnostics.CodeAnalysis.UnscopedRefAttribute") is not null;
		_overloadResolutionPriorityAttributePredefined = FindTypeSymbolIfAlreadyAvailable("System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute") is not null;
		_runtimeFeatureClass = (INamedTypeSymbol?)FindTypeSymbolIfAlreadyAvailable("System.Runtime.CompilerServices.RuntimeFeature");
		_comIIDInterfacePredefined = FindTypeSymbolIfAlreadyAvailable($"{Namespace}.{IComIIDGuidInterfaceName}") is not null;
		_getDelegateForFunctionPointerGenericExists = _compilation?.GetTypeByMetadataName(typeof(Marshal).FullName)?.GetMembers(nameof(Marshal.GetDelegateForFunctionPointer)).Any(m => m is IMethodSymbol { IsGenericMethod: true }) is true;
		_generateDefaultDllImportSearchPathsAttribute = _compilation?.GetTypeByMetadataName(typeof(DefaultDllImportSearchPathsAttribute).FullName) is object;
		if (FindTypeSymbolIfAlreadyAvailable("System.Runtime.Versioning.SupportedOSPlatformAttribute") is { } attribute)
		{
			_generateSupportedOSPlatformAttributes = true;
			AttributeData usageAttribute = attribute.GetAttributes().Single(att => att.AttributeClass?.Name == nameof(AttributeUsageAttribute));
			var targets = (AttributeTargets)usageAttribute.ConstructorArguments[0].Value!;
			_generateSupportedOSPlatformAttributesOnInterfaces = (targets & AttributeTargets.Interface) == AttributeTargets.Interface;
		}

		// Convert some of our CanUse fields to preprocessor symbols so our templates can use them.
		if (_parseOptions is not null)
		{
			List<string> extraSymbols = [];
			AddSymbolIf(_canUseSpan, "canUseSpan");
			AddSymbolIf(_canCallCreateSpan, "canCallCreateSpan");
			AddSymbolIf(_canUseUnsafeAsRef, "canUseUnsafeAsRef");
			AddSymbolIf(_canUseUnsafeAdd, "canUseUnsafeAdd");
			AddSymbolIf(_canUseUnsafeNullRef, "canUseUnsafeNullRef");
			AddSymbolIf(compilation?.GetTypeByMetadataName("System.Drawing.Point") is not null, "canUseSystemDrawing");
			AddSymbolIf(IsFeatureAvailable(Feature.InterfaceStaticMembers), "canUseInterfaceStaticMembers");
			AddSymbolIf(_canUseUnscopedRef, "canUseUnscopedRef");

			if (extraSymbols.Count > 0)
				_parseOptions = _parseOptions.WithPreprocessorSymbols(_parseOptions.PreprocessorSymbolNames.Concat(extraSymbols));

			void AddSymbolIf(bool condition, string symbol)
			{
				if (condition) extraSymbols.Add(symbol);
			}
		}

		bool useComInterfaces = options.AllowMarshaling;
		_generalTypeSettings = new TypeSyntaxSettings(
			this,
			PreferNativeInt: LanguageVersion >= LanguageVersion.CSharp9,
			PreferMarshaledTypes: false,
			AllowMarshaling: options.AllowMarshaling,
			QualifyNames: false);
		_fieldTypeSettings = _generalTypeSettings with { QualifyNames = true, IsField = true };
		_delegateSignatureTypeSettings = _generalTypeSettings with { QualifyNames = true };
		_enumTypeSettings = _generalTypeSettings;
		_fieldOfHandleTypeDefTypeSettings = _generalTypeSettings with { PreferNativeInt = false };
		_externSignatureTypeSettings = _generalTypeSettings with { QualifyNames = true, PreferMarshaledTypes = options.AllowMarshaling };
		_externReleaseSignatureTypeSettings = _externSignatureTypeSettings with { PreferNativeInt = false, PreferMarshaledTypes = false };
		_comSignatureTypeSettings = _generalTypeSettings with { QualifyNames = true, PreferInOutRef = options.AllowMarshaling };
		_extensionMethodSignatureTypeSettings = _generalTypeSettings with { QualifyNames = true };
		_functionPointerTypeSettings = _generalTypeSettings with { QualifyNames = true, AvoidWinmdRootAlias = true, AllowMarshaling = false };
		_errorMessageTypeSettings = _generalTypeSettings with { QualifyNames = true, Generator = null }; // Avoid risk of infinite recursion from errors in ToTypeSyntax

		_methodsAndConstantsClassName = IdentifierName(options.ClassName);

		FetchTemplate("ComHelpers", this, out _comHelperClass);
		FetchTemplate("VariableLengthInlineArray`1", this, out _variableLengthInlineArrayStruct1);
		FetchTemplate("VariableLengthInlineArray`2", this, out variableLengthInlineArrayStruct2);
	}

	/// <summary>
	/// Tests whether a string contains characters that do not belong in an API name.
	/// </summary>
	/// <param name="apiName">The user-supplied string that was expected to match some API name.</param>
	/// <returns><see langword="true"/> if the string contains characters that are likely mistakenly included and causing a mismatch; <see langword="false"/> otherwise.</returns>
	public static bool ContainsIllegalCharactersForAPIName(string apiName)
	{
		if (apiName is null)
			throw new ArgumentNullException(nameof(apiName));

		for (int i = 0; i < apiName.Length; i++)
		{
			char ch = apiName[i];
			bool allowed = false;
			allowed |= char.IsLetterOrDigit(ch);
			allowed |= ch == '_';
			allowed |= ch == '.'; // for qualified name searches

			if (!allowed)
				return true;
		}

		return false;
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	/// <inheritdoc/>
	public void GenerateAll(CancellationToken cancellationToken)
	{
		GenerateAllExternMethods(cancellationToken);
		GenerateAllInteropTypes(cancellationToken);
		GenerateAllConstants(cancellationToken);
		GenerateAllMacros(cancellationToken);
	}

	/// <inheritdoc/>
	public bool TryGenerate(string apiNameOrModuleWildcard, out IReadOnlyCollection<string> preciseApi, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(apiNameOrModuleWildcard))
			throw new ArgumentException("API cannot be null or empty.", nameof(apiNameOrModuleWildcard));

		if (apiNameOrModuleWildcard.EndsWith(".*", StringComparison.Ordinal))
		{
			if (TryGenerateAllExternMethods(apiNameOrModuleWildcard[..^2], cancellationToken))
			{
				preciseApi = ImmutableList.Create(apiNameOrModuleWildcard);
				return true;
			}
			else
			{
				preciseApi = [];
				return false;
			}
		}
		else if (apiNameOrModuleWildcard.EndsWith("*", StringComparison.Ordinal))
		{
			if (TryGenerateConstants(apiNameOrModuleWildcard))
			{
				preciseApi = ImmutableList.Create(apiNameOrModuleWildcard);
				return true;
			}
			else
			{
				preciseApi = ImmutableList<string>.Empty;
				return false;
			}
		}
		else
		{
			bool result = TryGenerateNamespace(apiNameOrModuleWildcard, out preciseApi);
			if (result || preciseApi.Count > 1)
			{
				return result;
			}

			result = TryGenerateExternMethod(apiNameOrModuleWildcard, out preciseApi);
			if (result || preciseApi.Count > 1)
			{
				return result;
			}

			result = TryGenerateType(apiNameOrModuleWildcard, out preciseApi);
			if (result || preciseApi.Count > 1)
			{
				return result;
			}

			result = TryGenerateConstant(apiNameOrModuleWildcard, out preciseApi);
			if (result || preciseApi.Count > 1)
			{
				return result;
			}

			result = TryGenerateMacro(apiNameOrModuleWildcard, out preciseApi);
			if (result || preciseApi.Count > 1)
			{
				return result;
			}

			return false;
		}
	}

	/// <summary>Generates all APIs within a given namespace, and their dependencies.</summary>
	/// <param name="namespace">The namespace to generate APIs for.</param>
	/// <param name="preciseApi">Receives the canonical API names that <paramref name="namespace"/> matched on.</param>
	/// <returns><see langword="true"/> if a matching namespace was found; otherwise <see langword="false"/>.</returns>
	public bool TryGenerateNamespace(string @namespace, out IReadOnlyCollection<string> preciseApi)
	{
		if (@namespace is null)
			throw new ArgumentNullException(nameof(@namespace));

		// Get the namespace metadata from the WinMD file.
		if (!WinMDIndexer.MetadataByNamespace.TryGetValue(@namespace, out NamespaceMetadata? metadata))
		{
			if (@namespace.StartsWith(WinMDIndexer.CommonNamespace, StringComparison.OrdinalIgnoreCase))
			{
				foreach (KeyValuePair<string, NamespaceMetadata> item in WinMDIndexer.MetadataByNamespace)
				{
					if (string.Equals(item.Key, @namespace, StringComparison.OrdinalIgnoreCase))
					{
						@namespace = item.Key;
						metadata = item.Value;
						break;
					}
				}
			}
		}

		if (metadata is not null)
		{
			_volatileCode.GenerationTransaction(delegate
			{
				foreach (KeyValuePair<string, MethodDefinitionHandle> method in metadata.Methods)
					RequestExternMethod(method.Value);

				foreach (KeyValuePair<string, TypeDefinitionHandle> type in metadata.Types)
					RequestInteropType(type.Value, DefaultContext);

				foreach (KeyValuePair<string, FieldDefinitionHandle> field in metadata.Fields)
					RequestConstant(field.Value);
			});

			preciseApi = ImmutableList.Create(@namespace);
			return true;
		}

		preciseApi = [];
		return false;
	}

	/// <inheritdoc/>
	public void GenerateAllMacros(CancellationToken cancellationToken)
	{
		// We only have macros to generate for the main SDK.
		if (!IsWin32Sdk)
			return;

		foreach (KeyValuePair<string, MethodDeclarationSyntax> macro in Win32SdkMacros)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				_volatileCode.GenerationTransaction(delegate
				{
					RequestMacro(macro.Value);
				});
			}
			catch (GenerationFailedException ex) when (IsPlatformCompatibleException(ex))
			{
				// Something transitively required for this field is not available for this platform, so skip this method.
			}
		}
	}

	/// <inheritdoc/>
	public void GenerateAllInteropTypes(CancellationToken cancellationToken)
	{
		foreach (TypeDefinitionHandle typeDefinitionHandle in WinMDReader.TypeDefinitions)
		{
			cancellationToken.ThrowIfCancellationRequested();
			TypeDefinition typeDef = WinMDReader.GetTypeDefinition(typeDefinitionHandle);
			if (typeDef.BaseType.IsNil && (typeDef.Attributes & TypeAttributes.Interface) != TypeAttributes.Interface)
				continue;

			// Ignore the attributes that describe the metadata.
			if (WinMDReader.StringComparer.Equals(typeDef.Namespace, InteropDecorationNamespace))
				continue;

			if (IsCompatibleWithPlatform(typeDef.GetCustomAttributes()))
			{
				try
				{
					_volatileCode.GenerationTransaction(delegate
					{
						RequestInteropType(typeDefinitionHandle, DefaultContext);
					});
				}
				catch (GenerationFailedException ex) when (IsPlatformCompatibleException(ex))
				{
					// Something transitively required for this type is not available for this platform, so skip this method.
				}
			}
		}
	}

	/// <inheritdoc/>
	public bool TryGenerateType(string possiblyQualifiedName, out IReadOnlyCollection<string> preciseApi)
	{
		if (possiblyQualifiedName is null)
			throw new ArgumentNullException(nameof(possiblyQualifiedName));

		TrySplitPossiblyQualifiedName(possiblyQualifiedName, out string? typeNamespace, out string typeName);
		IEnumerable<NamespaceMetadata>? namespaces = GetNamespacesToSearch(typeNamespace);
		bool foundApiWithMismatchedPlatform = false;

		List<TypeDefinitionHandle> matchingTypeHandles = [];
		foreach (NamespaceMetadata? nsMetadata in namespaces)
		{
			if (nsMetadata.Types.TryGetValue(typeName, out TypeDefinitionHandle handle))
				matchingTypeHandles.Add(handle);
			else if (nsMetadata.TypesForOtherPlatform.Contains(typeName))
				foundApiWithMismatchedPlatform = true;
		}

		if (matchingTypeHandles.Count is 1)
		{
			_volatileCode.GenerationTransaction(delegate
			{
				RequestInteropType(matchingTypeHandles[0], DefaultContext);
			});

			TypeDefinition typeDefinition = WinMDReader.GetTypeDefinition(matchingTypeHandles[0]);
			preciseApi = ImmutableList.Create($"{WinMDReader.GetString(typeDefinition.Namespace)}.{WinMDReader.GetString(typeDefinition.Name)}");
			return true;
		}
		else if (matchingTypeHandles.Count > 1)
		{
			preciseApi = ImmutableList.CreateRange(
				matchingTypeHandles.Select(h =>
				{
					TypeDefinition td = WinMDReader.GetTypeDefinition(h);
					return $"{WinMDReader.GetString(td.Namespace)}.{WinMDReader.GetString(td.Name)}";
				}));
			return false;
		}

		if (InputAssemblyName.Equals("Windows.Win32", StringComparison.OrdinalIgnoreCase) && SpecialTypeDefNames.Contains(typeName))
		{
			string? fullyQualifiedName = null;
			_volatileCode.GenerationTransaction(() => RequestSpecialTypeDefStruct(typeName, out fullyQualifiedName));
			preciseApi = ImmutableList.Create(fullyQualifiedName!);
			return true;
		}

		if (foundApiWithMismatchedPlatform)
			throw new PlatformIncompatibleException($"The requested API ({possiblyQualifiedName}) was found but is not available given the target platform ({_compilation?.Options.Platform}).");

		preciseApi = [];
		return false;
	}

	/// <summary>
	/// Generate code for the named macro, if it is recognized.
	/// </summary>
	/// <param name="macroName">The name of the macro. Never qualified with a namespace.</param>
	/// <param name="preciseApi">Receives the canonical API names that <paramref name="macroName"/> matched on.</param>
	/// <returns><see langword="true"/> if a match was found and the macro generated; otherwise <see langword="false"/>.</returns>
	public bool TryGenerateMacro(string macroName, out IReadOnlyCollection<string> preciseApi)
	{
		if (macroName is null)
			throw new ArgumentNullException(nameof(macroName));

		if (!IsWin32Sdk || !Win32SdkMacros.TryGetValue(macroName, out MethodDeclarationSyntax macro))
		{
			preciseApi = [];
			return false;
		}

		_volatileCode.GenerationTransaction(delegate
		{
			RequestMacro(macro);
		});

		preciseApi = ImmutableList.Create(macroName);
		return true;
	}

	/// <inheritdoc/>
	public IReadOnlyList<string> GetSuggestions(string name)
	{
		if (name is null)
			throw new ArgumentNullException(nameof(name));

		// Trim suffixes off the name.
		var suffixes = new List<string> { "A", "W", "32", "64", "Ex" };
		foreach (string suffix in suffixes)
		{
			if (name.EndsWith(suffix, StringComparison.Ordinal))
				name = name[..^suffix.Length];
		}

		// We should match on any API for which the given string is a substring.
		List<string> suggestions = [];
		foreach (NamespaceMetadata nsMetadata in WinMDIndexer.MetadataByNamespace.Values)
		{
			foreach (string candidate in nsMetadata.Fields.Keys.Concat(nsMetadata.Types.Keys).Concat(nsMetadata.Methods.Keys))
			{
				if (candidate.Contains(name))
					suggestions.Add(candidate);
			}
		}

		return suggestions;
	}

	/// <inheritdoc/>
	public IEnumerable<KeyValuePair<string, CompilationUnitSyntax>> GetCompilationUnits(CancellationToken cancellationToken)
	{
		if (_committedCode.IsEmpty)
			return ImmutableDictionary<string, CompilationUnitSyntax>.Empty;

		NamespaceDeclarationSyntax? starterNamespace = NamespaceDeclaration(ParseName(Namespace));

		// .g.cs because the resulting files are not user-created.
		const string FilenamePattern = "{0}.g.cs";
		Dictionary<string, CompilationUnitSyntax> results = new(StringComparer.OrdinalIgnoreCase);

		IEnumerable<MemberDeclarationSyntax> GroupMembersByNamespace(IEnumerable<MemberDeclarationSyntax> members)
		{
			return members.GroupBy(member =>
				member.HasAnnotations(NamespaceContainerAnnotation) ? member.GetAnnotations(NamespaceContainerAnnotation).Single().Data : null)
				.SelectMany(nsContents =>
					nsContents.Key is object
						? new MemberDeclarationSyntax[] { NamespaceDeclaration(ParseName(nsContents.Key)).AddMembers(nsContents.ToArray()) }
						: nsContents.ToArray());
		}

		if (_options.EmitSingleFile)
		{
			CompilationUnitSyntax file = CompilationUnit()
				.AddMembers(starterNamespace.AddMembers(GroupMembersByNamespace(NamespaceMembers).ToArray()))
				.AddMembers(_committedCode.GeneratedTopLevelTypes.ToArray());
			results.Add(
				string.Format(CultureInfo.InvariantCulture, FilenamePattern, "NativeMethods"),
				file);
		}
		else
		{
			foreach (MemberDeclarationSyntax topLevelType in _committedCode.GeneratedTopLevelTypes)
			{
				string typeName = topLevelType.DescendantNodesAndSelf().OfType<BaseTypeDeclarationSyntax>().First().Identifier.ValueText;
				results.Add(
					string.Format(CultureInfo.InvariantCulture, FilenamePattern, typeName),
					CompilationUnit().AddMembers(topLevelType));
			}

			IEnumerable<IGrouping<string?, MemberDeclarationSyntax>>? membersByFile = NamespaceMembers.GroupBy(
				member => member.HasAnnotations(SimpleFileNameAnnotation)
						? member.GetAnnotations(SimpleFileNameAnnotation).Single().Data
						: member switch
						{
							ClassDeclarationSyntax classDecl => classDecl.Identifier.ValueText,
							StructDeclarationSyntax structDecl => structDecl.Identifier.ValueText,
							InterfaceDeclarationSyntax ifaceDecl => ifaceDecl.Identifier.ValueText,
							EnumDeclarationSyntax enumDecl => enumDecl.Identifier.ValueText,
							DelegateDeclarationSyntax delegateDecl => "Delegates", // group all delegates in one file
							_ => throw new NotSupportedException("Unsupported member type: " + member.GetType().Name),
						},
				StringComparer.OrdinalIgnoreCase);

			foreach (IGrouping<string?, MemberDeclarationSyntax>? fileSimpleName in membersByFile)
			{
				try
				{
					CompilationUnitSyntax file = CompilationUnit()
						.AddMembers(starterNamespace.AddMembers(GroupMembersByNamespace(fileSimpleName).ToArray()));
					results.Add(
						string.Format(CultureInfo.InvariantCulture, FilenamePattern, fileSimpleName.Key),
						file);
				}
				catch (ArgumentException ex)
				{
					throw new GenerationFailedException($"Failed adding \"{fileSimpleName.Key}\".", ex);
				}
			}
		}

		var usingDirectives = new List<UsingDirectiveSyntax>
		{
			UsingDirective(AliasQualifiedName(IdentifierName(Token(SyntaxKind.GlobalKeyword)), IdentifierName(nameof(System)))),
			UsingDirective(AliasQualifiedName(IdentifierName(Token(SyntaxKind.GlobalKeyword)), IdentifierName(nameof(System) + "." + nameof(System.Diagnostics)))),
			UsingDirective(AliasQualifiedName(IdentifierName(Token(SyntaxKind.GlobalKeyword)), IdentifierName(nameof(System) + "." + nameof(System.Diagnostics) + "." + nameof(System.Diagnostics.CodeAnalysis)))),
			UsingDirective(ParseName(GlobalNamespacePrefix + SystemRuntimeCompilerServices)),
			UsingDirective(ParseName(GlobalNamespacePrefix + SystemRuntimeInteropServices)),
		};

		if (_generateSupportedOSPlatformAttributes)
		{
			usingDirectives.Add(UsingDirective(ParseName(GlobalNamespacePrefix + "System.Runtime.Versioning")));
		}

		usingDirectives.Add(UsingDirective(NameEquals(GlobalWinmdRootNamespaceAlias), ParseName(GlobalNamespacePrefix + WinMDIndexer.CommonNamespace)));

		var normalizedResults = new Dictionary<string, CompilationUnitSyntax>(StringComparer.OrdinalIgnoreCase);
		results.AsParallel().WithCancellation(cancellationToken).ForAll(kv =>
		{
			CompilationUnitSyntax? compilationUnit = ((CompilationUnitSyntax)kv.Value
				.AddUsings(usingDirectives.ToArray())
				.Accept(new WhitespaceRewriter())!)
				.WithLeadingTrivia(FileHeader);

			lock (normalizedResults)
			{
				normalizedResults.Add(kv.Key, compilationUnit);
			}
		});

		if (_compilation?.GetTypeByMetadataName("System.Reflection.AssemblyMetadataAttribute") is not null)
		{
			if (_options.EmitSingleFile)
			{
				KeyValuePair<string, CompilationUnitSyntax> originalEntry = normalizedResults.Single();
				normalizedResults[originalEntry.Key] = originalEntry.Value.WithLeadingTrivia().AddAttributeLists(CsWin32StampAttribute).WithLeadingTrivia(originalEntry.Value.GetLeadingTrivia());
			}
			else
			{
				normalizedResults.Add(string.Format(CultureInfo.InvariantCulture, FilenamePattern, "CsWin32Stamp"), CompilationUnit().AddAttributeLists(CsWin32StampAttribute).WithLeadingTrivia(FileHeader));
			}
		}

		if (_committedCode.NeedsWinRTCustomMarshaler)
		{
			string? marshalerText = FetchTemplateText(WinRTCustomMarshalerClass);
			if (marshalerText is null)
			{
				throw new GenerationFailedException($"Failed to get template for \"{WinRTCustomMarshalerClass}\".");
			}

			SyntaxTree? marshalerContents = SyntaxFactory.ParseSyntaxTree(marshalerText, cancellationToken: cancellationToken);
			if (marshalerContents is null)
			{
				throw new GenerationFailedException($"Failed adding \"{WinRTCustomMarshalerClass}\".");
			}

			CompilationUnitSyntax? compilationUnit = ((CompilationUnitSyntax)marshalerContents.GetRoot(cancellationToken))
				.WithLeadingTrivia(ParseLeadingTrivia(AutoGeneratedHeader));

			normalizedResults.Add(
				string.Format(CultureInfo.InvariantCulture, FilenamePattern, WinRTCustomMarshalerClass),
				compilationUnit);
		}

		return normalizedResults;
	}

	internal static ImmutableDictionary<string, string> GetBannedAPIs(GeneratorOptions options) => options.AllowMarshaling ? BannedAPIsWithMarshaling : BannedAPIsWithoutMarshaling;

	/// <summary>
	/// Checks whether an exception was originally thrown because of a target platform incompatibility.
	/// </summary>
	/// <param name="ex">An exception that may be or contain a <see cref="PlatformIncompatibleException"/>.</param>
	/// <returns><see langword="true"/> if <paramref name="ex"/> or an inner exception is a <see cref="PlatformIncompatibleException"/>; otherwise <see langword="false" />.</returns>
	internal static bool IsPlatformCompatibleException(Exception? ex)
	{
		if (ex is null)
			return false;

		return ex is PlatformIncompatibleException || IsPlatformCompatibleException(ex?.InnerException);
	}

	internal static string ReplaceCommonNamespaceWithAlias(Generator? generator, string fullNamespace)
	{
		return generator is not null && generator.TryStripCommonNamespace(fullNamespace, out string? stripped) ? (stripped.Length > 0 ? $"{GlobalWinmdRootNamespaceAlias}.{stripped}" : GlobalWinmdRootNamespaceAlias) : $"global::{fullNamespace}";
	}

	internal void RequestComHelpers(Context context)
	{
		if (IsWin32Sdk)
		{
			if (!IsTypeAlreadyFullyDeclared($"{Namespace}.{_comHelperClass.Identifier.ValueText}"))
			{
				RequestInteropType("Windows.Win32.Foundation", "HRESULT", context);
				_volatileCode.GenerateSpecialType("ComHelpers", () => _volatileCode.AddSpecialType("ComHelpers", _comHelperClass));
			}

			if (IsFeatureAvailable(Feature.InterfaceStaticMembers) && !context.AllowMarshaling)
			{
				if (!IsTypeAlreadyFullyDeclared($"{Namespace}.{IVTableInterface.Identifier.ValueText}"))
				{
					_volatileCode.GenerateSpecialType("IVTable", () => _volatileCode.AddSpecialType("IVTable", IVTableInterface));
				}

				if (!IsTypeAlreadyFullyDeclared($"{Namespace}.{IVTableGenericInterface.Identifier.ValueText}`2"))
				{
					_volatileCode.GenerateSpecialType("IVTable`2", () => _volatileCode.AddSpecialType("IVTable`2", IVTableGenericInterface));
				}

				if (!TryGenerate("IUnknown", out _, default))
				{
					throw new GenerationFailedException("Unable to generate IUnknown.");
				}
			}
		}
		else if (Manager is not null && Manager.TryGetGenerator("Windows.Win32", out Generator? generator))
		{
			generator.RequestComHelpers(context);
		}
	}

	internal bool TryStripCommonNamespace(string fullNamespace, [NotNullWhen(true)] out string? strippedNamespace)
	{
		if (fullNamespace.StartsWith(WinMDIndexer.CommonNamespaceWithDot, StringComparison.Ordinal))
		{
			strippedNamespace = fullNamespace.Substring(WinMDIndexer.CommonNamespaceWithDot.Length);
			return true;
		}
		else if (fullNamespace == WinMDIndexer.CommonNamespace)
		{
			strippedNamespace = string.Empty;
			return true;
		}

		strippedNamespace = null;
		return false;
	}

	internal void RequestInteropType(string @namespace, string name, Context context)
	{
		// PERF: Skip this search if this namespace/name has already been generated (committed, or still in volatileCode).
		foreach (TypeDefinitionHandle tdh in WinMDReader.TypeDefinitions)
		{
			TypeDefinition td = WinMDReader.GetTypeDefinition(tdh);
			if (WinMDReader.StringComparer.Equals(td.Name, name) && WinMDReader.StringComparer.Equals(td.Namespace, @namespace))
			{
				_volatileCode.GenerationTransaction(delegate
				{
					RequestInteropType(tdh, context);
				});

				return;
			}
		}

		throw new GenerationFailedException($"Referenced type \"{@namespace}.{name}\" not found in \"{InputAssemblyName}\".");
	}

	internal void RequestInteropType(TypeDefinitionHandle typeDefHandle, Context context)
	{
		TypeDefinition typeDef = WinMDReader.GetTypeDefinition(typeDefHandle);
		if (typeDef.GetDeclaringType() is { IsNil: false } nestingParentHandle)
		{
			// We should only generate this type into its parent type.
			RequestInteropType(nestingParentHandle, context);
			return;
		}

		string ns = WinMDReader.GetString(typeDef.Namespace);
		if (!IsCompatibleWithPlatform(typeDef.GetCustomAttributes()))
		{
			// We've been asked for an interop type that does not apply. This happens because the metadata
			// may use a TypeReferenceHandle or TypeDefinitionHandle to just one of many arch-specific definitions of this type.
			// Try to find the appropriate definition for our target architecture.
			string name = WinMDReader.GetString(typeDef.Name);
			NamespaceMetadata namespaceMetadata = WinMDIndexer.MetadataByNamespace[ns];
			if (!namespaceMetadata.Types.TryGetValue(name, out typeDefHandle) && namespaceMetadata.TypesForOtherPlatform.Contains(name))
			{
				throw new PlatformIncompatibleException($"Request for type ({ns}.{name}) that is not available given the target platform.");
			}
		}

		bool hasUnmanagedName = HasUnmanagedSuffix(WinMDReader, typeDef.Name, context.AllowMarshaling, IsManagedType(typeDefHandle));
		_volatileCode.GenerateType(typeDefHandle, hasUnmanagedName, delegate
		{
			if (RequestInteropTypeHelper(typeDefHandle, context) is MemberDeclarationSyntax typeDeclaration)
			{
				if (!TryStripCommonNamespace(ns, out string? shortNamespace))
				{
					throw new GenerationFailedException("Unexpected namespace: " + ns);
				}

				if (shortNamespace.Length > 0)
				{
					typeDeclaration = typeDeclaration.WithAdditionalAnnotations(
						new SyntaxAnnotation(NamespaceContainerAnnotation, shortNamespace));
				}

				_volatileCode.AddInteropType(typeDefHandle, hasUnmanagedName, typeDeclaration);
			}
		});
	}

	internal void RequestInteropType(TypeReferenceHandle typeRefHandle, Context context)
	{
		if (TryGetTypeDefHandle(typeRefHandle, out TypeDefinitionHandle typeDefHandle))
		{
			RequestInteropType(typeDefHandle, context);
		}
		else
		{
			TypeReference typeRef = WinMDReader.GetTypeReference(typeRefHandle);
			if (typeRef.ResolutionScope.Kind == HandleKind.AssemblyReference)
			{
				if (Manager?.TryRequestInteropType(new(this, typeRef), context) is not true)
				{
					// We can't find the interop among our metadata inputs.
					// Before we give up and report an error, search for the required type among the compilation's referenced assemblies.
					string metadataName = $"{WinMDReader.GetString(typeRef.Namespace)}.{WinMDReader.GetString(typeRef.Name)}";
					if (_compilation?.GetTypeByMetadataName(metadataName) is null)
					{
						AssemblyReference assemblyRef = WinMDReader.GetAssemblyReference((AssemblyReferenceHandle)typeRef.ResolutionScope);
						string scope = WinMDReader.GetString(assemblyRef.Name);
						throw new GenerationFailedException($"Input metadata _memoryMappedFile \"{scope}\" has not been provided, or is referenced at a version that is lacking the type \"{metadataName}\".");
					}
				}
			}
		}
	}

	internal void RequestMacro(MethodDeclarationSyntax macro)
	{
		_volatileCode.GenerateMacro(macro.Identifier.ValueText, delegate
		{
			_volatileCode.AddMacro(macro.Identifier.ValueText, (MethodDeclarationSyntax)ElevateVisibility(macro));

			// Generate any additional types that this macro relies on.
			foreach (QualifiedNameSyntax identifier in macro.DescendantNodes().OfType<QualifiedNameSyntax>())
			{
				string identifierString = identifier.ToString();
				if (identifierString.StartsWith(GlobalNamespacePrefix, StringComparison.Ordinal))
				{
					TryGenerateType(identifierString.Substring(GlobalNamespacePrefix.Length), out _);
				}
			}

			// Generate macro dependencies, if any.
			foreach (IdentifierNameSyntax identifier in macro.DescendantNodes().OfType<IdentifierNameSyntax>())
			{
				string identifierString = identifier.ToString();
				if (Win32SdkMacros.ContainsKey(identifierString))
				{
					TryGenerateMacro(identifierString, out _);
				}
			}
		});
	}

	internal void GetBaseTypeInfo(TypeDefinition typeDef, out StringHandle baseTypeName, out StringHandle baseTypeNamespace)
	{
		if (typeDef.BaseType.IsNil)
		{
			baseTypeName = default;
			baseTypeNamespace = default;
		}
		else
		{
			switch (typeDef.BaseType.Kind)
			{
				case HandleKind.TypeReference:
					TypeReference baseTypeRef = WinMDReader.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
					baseTypeName = baseTypeRef.Name;
					baseTypeNamespace = baseTypeRef.Namespace;
					break;
				case HandleKind.TypeDefinition:
					TypeDefinition baseTypeDef = WinMDReader.GetTypeDefinition((TypeDefinitionHandle)typeDef.BaseType);
					baseTypeName = baseTypeDef.Name;
					baseTypeNamespace = baseTypeDef.Namespace;
					break;
				default:
					throw new NotSupportedException("Unsupported base type handle: " + typeDef.BaseType.Kind);
			}
		}
	}

	internal MemberDeclarationSyntax? RequestSpecialTypeDefStruct(string specialName, out string fullyQualifiedName)
	{
		string subNamespace = "Foundation";
		string ns = $"{Namespace}.{subNamespace}";
		fullyQualifiedName = $"{ns}.{specialName}";

		if (IsTypeAlreadyFullyDeclared(fullyQualifiedName))
		{
			// The type already exists either in this project or a referenced one.
			return null;
		}

		MemberDeclarationSyntax? specialDeclaration = null;
		if (InputAssemblyName.Equals("Windows.Win32", StringComparison.OrdinalIgnoreCase))
		{
			_volatileCode.GenerateSpecialType(specialName, delegate
			{
				switch (specialName)
				{
					case "PCWSTR":
					case "PCSTR":
					case "PCZZSTR":
					case "PCZZWSTR":
					case "PZZSTR":
					case "PZZWSTR":
						specialDeclaration = FetchTemplate($"{specialName}");
						if (!specialName.StartsWith("PC", StringComparison.Ordinal))
						{
							TryGenerateType("Windows.Win32.Foundation.PC" + specialName.Substring(1), out _); // the template references its constant version
						}
						else if (specialName.StartsWith("PCZZ", StringComparison.Ordinal))
						{
							TryGenerateType("Windows.Win32.Foundation.PC" + specialName.Substring(4), out _); // the template references its single string version
						}

						break;
					default:
						throw new ArgumentException($"This special name is not recognized: \"{specialName}\".", nameof(specialName));
				}

				if (specialDeclaration is null)
				{
					throw new GenerationFailedException("Failed to parse template.");
				}

				specialDeclaration = specialDeclaration.WithAdditionalAnnotations(new SyntaxAnnotation(NamespaceContainerAnnotation, subNamespace));

				_volatileCode.AddSpecialType(specialName, specialDeclaration);
			});
		}
		else if (Manager?.TryGetGenerator("Windows.Win32", out Generator? win32Generator) is true)
		{
			string? fullyQualifiedNameLocal = null!;
			win32Generator._volatileCode.GenerationTransaction(delegate
			{
				specialDeclaration = win32Generator.RequestSpecialTypeDefStruct(specialName, out fullyQualifiedNameLocal);
			});
			fullyQualifiedName = fullyQualifiedNameLocal;
		}

		return specialDeclaration;
	}

	internal bool HasUnmanagedSuffix(string originalName, bool allowMarshaling, bool isManagedType) => !allowMarshaling && isManagedType && _options.AllowMarshaling && originalName is not "IUnknown";

	internal bool HasUnmanagedSuffix(MetadataReader reader, StringHandle typeName, bool allowMarshaling, bool isManagedType) => !allowMarshaling && isManagedType && _options.AllowMarshaling && !reader.StringComparer.Equals(typeName, "IUnknown");

	internal string GetMangledIdentifier(string normalIdentifier, bool allowMarshaling, bool isManagedType) =>
		HasUnmanagedSuffix(normalIdentifier, allowMarshaling, isManagedType) ? normalIdentifier + UnmanagedInteropSuffix : normalIdentifier;

	/// <summary>
	/// Disposes of managed and unmanaged resources.
	/// </summary>
	/// <param name="disposing"><see langword="true"/> if being disposed.</param>
	protected virtual void Dispose(bool disposing)
	{
		if (disposing)
		{
			_winMDReaderRental.Dispose();
		}
	}

	/// <summary>
	/// Checks for periods in a name and if found, splits off the last element as the name and considers everything before it to be a namespace.
	/// </summary>
	/// <param name="possiblyQualifiedName">A name or qualified name (e.g. "String" or "System.String").</param>
	/// <param name="namespace">Receives the namespace portion if present in <paramref name="possiblyQualifiedName"/> (e.g. "System"); otherwise <see langword="null"/>.</param>
	/// <param name="name">Receives the name portion from <paramref name="possiblyQualifiedName"/>.</param>
	/// <returns>A value indicating whether a namespace was present in <paramref name="possiblyQualifiedName"/>.</returns>
	private static bool TrySplitPossiblyQualifiedName(string possiblyQualifiedName, [NotNullWhen(true)] out string? @namespace, out string name)
	{
		int nameIdx = possiblyQualifiedName.LastIndexOf('.');
		@namespace = nameIdx >= 0 ? possiblyQualifiedName.Substring(0, nameIdx) : null;
		name = nameIdx >= 0 ? possiblyQualifiedName.Substring(nameIdx + 1) : possiblyQualifiedName;
		return @namespace is object;
	}

	private static NativeArrayInfo DecodeNativeArrayInfoAttribute(CustomAttribute nativeArrayInfoAttribute)
	{
		CustomAttributeValue<TypeSyntax> args = nativeArrayInfoAttribute.DecodeValue(CustomAttributeTypeProvider.Instance);
		return new NativeArrayInfo
		{
			CountConst = (int?)args.NamedArguments.FirstOrDefault(a => a.Name == "CountConst").Value,
			CountParamIndex = (short?)args.NamedArguments.FirstOrDefault(a => a.Name == "CountParamIndex").Value,
		};
	}

	private static MemorySize DecodeMemorySizeAttribute(CustomAttribute memorySizeAttribute)
	{
		CustomAttributeValue<TypeSyntax> args = memorySizeAttribute.DecodeValue(CustomAttributeTypeProvider.Instance);
		return new MemorySize
		{
			BytesParamIndex = (short?)args.NamedArguments.FirstOrDefault(a => a.Name == "BytesParamIndex").Value,
		};
	}

	private bool TryGetRenamedMethod(string methodName, [NotNullWhen(true)] out string? newName)
	{
		if (WideCharOnly && IsWideFunction(methodName))
		{
			newName = methodName.Substring(0, methodName.Length - 1);
			return !GetMethodByName(newName, exactNameMatchOnly: true).HasValue;
		}

		newName = null;
		return false;
	}

	/// <summary>
	/// Checks whether a type with the given name is already defined in the compilation
	/// such that we must (or should) skip generating it ourselves.
	/// </summary>
	/// <param name="fullyQualifiedMetadataName">The fully-qualified metadata name of the type.</param>
	/// <returns><see langword="true"/> if the type should <em>not</em> be emitted; <see langword="false" /> if the type is not already declared in the compilation.</returns>
	/// <remarks>
	/// Skip if the compilation already defines this type or can access it from elsewhere.
	/// But if we have more than one match, the compiler won't be able to resolve our type references.
	/// In such a case, we'll prefer to just declare our own local symbol.
	/// </remarks>
	private bool IsTypeAlreadyFullyDeclared(string fullyQualifiedMetadataName) => FindTypeSymbolsIfAlreadyAvailable(fullyQualifiedMetadataName).Count == 1;

	private ISymbol? FindTypeSymbolIfAlreadyAvailable(string fullyQualifiedMetadataName) => FindTypeSymbolsIfAlreadyAvailable(fullyQualifiedMetadataName).FirstOrDefault();

	private IReadOnlyList<ISymbol> FindTypeSymbolsIfAlreadyAvailable(string fullyQualifiedMetadataName)
	{
		if (_findTypeSymbolIfAlreadyAvailableCache.TryGetValue(fullyQualifiedMetadataName, out IReadOnlyList<ISymbol>? result))
		{
			return result;
		}

		List<ISymbol>? results = null;
		if (_compilation is object)
		{
			if (_compilation.Assembly.GetTypeByMetadataName(fullyQualifiedMetadataName) is { } ownSymbol)
			{
				// This assembly defines it.
				// But if it defines it as a partial, we should not consider it as fully defined so we populate our side.
				if (!ownSymbol.DeclaringSyntaxReferences.Any(sr => sr.GetSyntax() is BaseTypeDeclarationSyntax type && type.Modifiers.Any(SyntaxKind.PartialKeyword)))
				{
					results ??= new();
					results.Add(ownSymbol);
				}
			}

			foreach (MetadataReference? reference in _compilation.References)
			{
				if (!reference.Properties.Aliases.IsEmpty)
				{
					// We don't (yet) generate code to leverage aliases, so we skip any symbols defined in aliased references.
					continue;
				}

				if (_compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol referencedAssembly)
				{
					if (referencedAssembly.GetTypeByMetadataName(fullyQualifiedMetadataName) is { } externalSymbol)
					{
						if (_compilation.IsSymbolAccessibleWithin(externalSymbol, _compilation.Assembly))
						{
							// A referenced assembly declares this symbol and it is accessible to our own.
							results ??= new();
							results.Add(externalSymbol);
						}
					}
				}
			}
		}

		result = (IReadOnlyList<ISymbol>?)results ?? Array.Empty<ISymbol>();
		_findTypeSymbolIfAlreadyAvailableCache.Add(fullyQualifiedMetadataName, result);
		return result;
	}

	private ISymbol? FindExtensionMethodIfAlreadyAvailable(string fullyQualifiedTypeMetadataName, string methodName)
	{
		foreach (INamedTypeSymbol typeSymbol in FindTypeSymbolsIfAlreadyAvailable(fullyQualifiedTypeMetadataName).OfType<INamedTypeSymbol>())
		{
			if (typeSymbol.GetMembers(methodName) is { Length: > 0 } members)
			{
				return members[0];
			}
		}

		return null;
	}

	private MemberDeclarationSyntax? RequestInteropTypeHelper(TypeDefinitionHandle typeDefHandle, Context context)
	{
		TypeDefinition typeDef = WinMDReader.GetTypeDefinition(typeDefHandle);
		if (IsCompilerGenerated(typeDef))
		{
			return null;
		}

		// Skip if the compilation already defines this type or can access it from elsewhere.
		string name = WinMDReader.GetString(typeDef.Name);
		string ns = WinMDReader.GetString(typeDef.Namespace);
		bool isManagedType = IsManagedType(typeDefHandle);
		string fullyQualifiedName = GetMangledIdentifier(ns + "." + name, context.AllowMarshaling, isManagedType);

		// Skip if the compilation already defines this type or can access it from elsewhere.
		// But if we have more than one match, the compiler won't be able to resolve our type references.
		// In such a case, we'll prefer to just declare our own local symbol.
		if (IsTypeAlreadyFullyDeclared(fullyQualifiedName))
		{
			// The type already exists either in this project or a referenced one.
			return null;
		}

		try
		{
			StringHandle baseTypeName, baseTypeNamespace;
			GetBaseTypeInfo(typeDef, out baseTypeName, out baseTypeNamespace);

			MemberDeclarationSyntax? typeDeclaration;

			if ((typeDef.Attributes & TypeAttributes.Interface) == TypeAttributes.Interface)
			{
				typeDeclaration = DeclareInterface(typeDefHandle, context);
			}
			else if (WinMDReader.StringComparer.Equals(baseTypeName, nameof(ValueType)) && WinMDReader.StringComparer.Equals(baseTypeNamespace, nameof(System)))
			{
				// Is this a special typedef struct?
				if (IsTypeDefStruct(typeDef))
				{
					typeDeclaration = DeclareTypeDefStruct(typeDef, typeDefHandle);
				}
				else if (IsEmptyStructWithGuid(typeDef))
				{
					typeDeclaration = DeclareCocreatableClass(typeDef);
				}
				else
				{
					StructDeclarationSyntax structDeclaration = DeclareStruct(typeDefHandle, context);

					// Proactively generate all nested types as well.
					// If the outer struct is using ExplicitLayout, generate the nested types as unmanaged structs since that's what will be needed.
					Context nestedContext = context;
					bool explicitLayout = (typeDef.Attributes & TypeAttributes.ExplicitLayout) == TypeAttributes.ExplicitLayout;
					if (context.AllowMarshaling && explicitLayout)
					{
						nestedContext = nestedContext with { AllowMarshaling = false };
					}

					foreach (TypeDefinitionHandle nestedHandle in typeDef.GetNestedTypes())
					{
						if (RequestInteropTypeHelper(nestedHandle, nestedContext) is { } nestedType)
						{
							structDeclaration = structDeclaration.AddMembers(nestedType);
						}
					}

					typeDeclaration = structDeclaration;
				}
			}
			else if (WinMDReader.StringComparer.Equals(baseTypeName, nameof(Enum)) && WinMDReader.StringComparer.Equals(baseTypeNamespace, nameof(System)))
			{
				// Consider reusing .NET types like FILE_SHARE_FLAGS -> System.IO.FileShare
				typeDeclaration = DeclareEnum(typeDef);
			}
			else if (WinMDReader.StringComparer.Equals(baseTypeName, nameof(MulticastDelegate)) && WinMDReader.StringComparer.Equals(baseTypeNamespace, nameof(System)))
			{
				typeDeclaration =
					IsUntypedDelegate(typeDef) ? DeclareUntypedDelegate(typeDef) :
					_options.AllowMarshaling ? DeclareDelegate(typeDef) :
					null;
			}
			else
			{
				// not yet supported.
				return null;
			}

			// add generated code attribute.
			if (typeDeclaration is not null)
			{
				typeDeclaration = typeDeclaration
					.WithLeadingTrivia()
					.AddAttributeLists(AttributeList().AddAttributes(GeneratedCodeAttribute))
					.WithLeadingTrivia(typeDeclaration.GetLeadingTrivia());
			}

			return typeDeclaration;
		}
		catch (Exception ex)
		{
			throw new GenerationFailedException($"Failed to generate {WinMDReader.GetString(typeDef.Name)}{(context.AllowMarshaling ? string.Empty : " (unmanaged)")}", ex);
		}
	}

	private bool IsCompatibleWithPlatform(CustomAttributeHandleCollection customAttributesOnMember) => WinMDFileHelper.IsCompatibleWithPlatform(WinMDReader, WinMDIndexer, _compilation?.Options.Platform, customAttributesOnMember);

	private void TryGenerateTypeOrThrow(string possiblyQualifiedName)
	{
		if (!TryGenerateType(possiblyQualifiedName, out _))
		{
			throw new GenerationFailedException("Unable to find expected type: " + possiblyQualifiedName);
		}
	}

	private void TryGenerateConstantOrThrow(string possiblyQualifiedName)
	{
		if (!TryGenerateConstant(possiblyQualifiedName, out _))
		{
			throw new GenerationFailedException("Unable to find expected constant: " + possiblyQualifiedName);
		}
	}

	private MethodDeclarationSyntax CreateAsSpanMethodOverValueAndLength(TypeSyntax spanType)
	{
		ExpressionSyntax thisValue = MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, ThisExpression(), IdentifierName("Value"));
		ExpressionSyntax thisLength = MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, ThisExpression(), IdentifierName("Length"));

		// internal X AsSpan() => Value is null ? default(X) : new X(Value, Length);
		return MethodDeclaration(spanType, Identifier("AsSpan"))
			.AddModifiers(TokenWithSpace(Visibility))
			.WithExpressionBody(ArrowExpressionClause(ConditionalExpression(
				condition: IsPatternExpression(thisValue, ConstantPattern(LiteralExpression(SyntaxKind.NullLiteralExpression))),
				whenTrue: DefaultExpression(spanType),
				whenFalse: ObjectCreationExpression(spanType).AddArgumentListArguments(Argument(thisValue), Argument(thisLength)))))
			.WithSemicolonToken(SemicolonWithLineFeed)
			.WithLeadingTrivia(StrAsSpanComment);
	}

	private string GetNormalizedModuleName(MethodImport import)
	{
		ModuleReference module = WinMDReader.GetModuleReference(import.Module);
		string moduleName = WinMDReader.GetString(module.Name);
		if (CanonicalCapitalizations.TryGetValue(moduleName, out string? canonicalModuleName))
		{
			moduleName = canonicalModuleName;
		}

		return moduleName;
	}

	private string GetNamespaceForPossiblyNestedType(TypeDefinition nestedTypeDef)
	{
		if (nestedTypeDef.IsNested)
		{
			return GetNamespaceForPossiblyNestedType(WinMDReader.GetTypeDefinition(nestedTypeDef.GetDeclaringType()));
		}
		else
		{
			return WinMDReader.GetString(nestedTypeDef.Namespace);
		}
	}

	private ParameterListSyntax CreateParameterList(MethodDefinition methodDefinition, MethodSignature<TypeHandleInfo> signature, TypeSyntaxSettings typeSettings, GeneratingElement forElement)
		=> FixTrivia(ParameterList().AddParameters(methodDefinition.GetParameters().Select(WinMDReader.GetParameter).Where(p => !p.Name.IsNil).Select(p => CreateParameter(signature.ParameterTypes[p.SequenceNumber - 1], p, typeSettings, forElement)).ToArray()));

	private ParameterSyntax CreateParameter(TypeHandleInfo parameterInfo, Parameter parameter, TypeSyntaxSettings typeSettings, GeneratingElement forElement)
	{
		string name = WinMDReader.GetString(parameter.Name);
		try
		{
			// TODO:
			// * Notice [Out][RAIIFree] handle producing parameters. Can we make these provide SafeHandle's?
			bool isReturnOrOutParam = parameter.SequenceNumber == 0 || (parameter.Attributes & ParameterAttributes.Out) == ParameterAttributes.Out;
			TypeSyntaxAndMarshaling parameterTypeSyntax = parameterInfo.ToTypeSyntax(typeSettings, forElement, parameter.GetCustomAttributes(), parameter.Attributes);

			// Determine the custom attributes to apply.
			AttributeListSyntax? attributes = AttributeList();
			if (parameterTypeSyntax.Type is PointerTypeSyntax ptr)
			{
				if ((parameter.Attributes & ParameterAttributes.Optional) == ParameterAttributes.Optional)
				{
					attributes = attributes.AddAttributes(OptionalAttributeSyntax);
				}
			}

			SyntaxTokenList modifiers = TokenList();
			if (parameterTypeSyntax.ParameterModifier.HasValue)
			{
				modifiers = modifiers.Add(parameterTypeSyntax.ParameterModifier.Value.WithTrailingTrivia(TriviaList(Space)));
			}

			if (parameterTypeSyntax.MarshalAsAttribute is object)
			{
				if ((parameter.Attributes & ParameterAttributes.Out) == ParameterAttributes.Out)
				{
					if ((parameter.Attributes & ParameterAttributes.In) == ParameterAttributes.In)
					{
						attributes = attributes.AddAttributes(InAttributeSyntax);
					}

					if (!modifiers.Any(SyntaxKind.OutKeyword))
					{
						attributes = attributes.AddAttributes(OutAttributeSyntax);
					}
				}
			}

			ParameterSyntax parameterSyntax = Parameter(
				attributes.Attributes.Count > 0 ? List<AttributeListSyntax>().Add(attributes) : List<AttributeListSyntax>(),
				modifiers,
				parameterTypeSyntax.Type.WithTrailingTrivia(TriviaList(Space)),
				SafeIdentifier(name),
				@default: null);
			parameterSyntax = parameterTypeSyntax.AddMarshalAs(parameterSyntax);

			if (FindInteropDecorativeAttribute(parameter.GetCustomAttributes(), "RetValAttribute") is not null)
			{
				parameterSyntax = parameterSyntax.WithAdditionalAnnotations(IsRetValAnnotation);
			}

			return parameterSyntax;
		}
		catch (Exception ex)
		{
			throw new GenerationFailedException("Failed while generating parameter: " + name, ex);
		}
	}

	private void DeclareSliceAtNullExtensionMethodIfNecessary()
	{
		if (_sliceAtNullMethodDecl is null)
		{
			IdentifierNameSyntax valueParam = IdentifierName("value");
			IdentifierNameSyntax lengthLocal = IdentifierName("length");
			TypeSyntax charSpan = MakeReadOnlySpanOfT(PredefinedType(Token(SyntaxKind.CharKeyword)));

			// int length = value.IndexOf('\0');
			StatementSyntax lengthLocalDeclaration =
				LocalDeclarationStatement(VariableDeclaration(PredefinedType(TokenWithSpace(SyntaxKind.IntKeyword))).AddVariables(
					VariableDeclarator(lengthLocal.Identifier).WithInitializer(EqualsValueClause(
						InvocationExpression(
							MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, valueParam, IdentifierName(nameof(MemoryExtensions.IndexOf))),
							ArgumentList().AddArguments(Argument(LiteralExpression(SyntaxKind.CharacterLiteralExpression, Literal('\0')))))))));

			// static ReadOnlySpan<char> SliceAtNull(this ReadOnlySpan<char> value)
			_sliceAtNullMethodDecl = MethodDeclaration(charSpan, SliceAtNullMethodName.Identifier)
				.AddModifiers(TokenWithSpace(Visibility), TokenWithSpace(SyntaxKind.StaticKeyword))
				.AddParameterListParameters(Parameter(valueParam.Identifier).WithType(charSpan).AddModifiers(TokenWithSpace(SyntaxKind.ThisKeyword)))
				.WithBody(Block().AddStatements(
					lengthLocalDeclaration,
					//// return length < 0 ? value : value.Slice(0, length);
					ReturnStatement(ConditionalExpression(
						BinaryExpression(SyntaxKind.LessThanExpression, lengthLocal, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))),
						valueParam,
						InvocationExpression(
							MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, valueParam, IdentifierName(nameof(ReadOnlySpan<char>.Slice))),
							ArgumentList().AddArguments(Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))), Argument(lengthLocal)))))));
		}

		_volatileCode.AddInlineArrayIndexerExtension(_sliceAtNullMethodDecl);
	}

	private IEnumerable<NamespaceMetadata> GetNamespacesToSearch(string? @namespace)
	{
		if (@namespace is not null)
		{
			return WinMDIndexer.MetadataByNamespace.TryGetValue(@namespace, out NamespaceMetadata? metadata)
				? new[] { metadata }
				: [];
		}
		else
		{
			return WinMDIndexer.MetadataByNamespace.Values;
		}
	}

	[DebuggerDisplay($"AllowMarshaling: {{{nameof(AllowMarshaling)}}}")]
	internal record struct Context
	{
		/// <summary>
		/// Gets a value indicating whether the context permits marshaling.
		/// This may be more constrained than <see cref="GeneratorOptions.AllowMarshaling"/> when within the context of a union struct.
		/// </summary>
		internal bool AllowMarshaling { get; init; }

		internal TypeSyntaxSettings Filter(TypeSyntaxSettings settings)
		{
			if (!AllowMarshaling && settings.AllowMarshaling)
			{
				settings = settings with { AllowMarshaling = false };
			}

			return settings;
		}
	}

	internal struct NativeArrayInfo
	{
		internal short? CountParamIndex { get; init; }

		internal int? CountConst { get; init; }
	}

	internal struct MemorySize
	{
		internal short? BytesParamIndex { get; init; }
	}

	private class DirectiveTriviaRemover : CSharpSyntaxRewriter
	{
		internal static readonly DirectiveTriviaRemover Instance = new();

		private DirectiveTriviaRemover()
		{
		}

		public override SyntaxTrivia VisitTrivia(SyntaxTrivia trivia) =>
			trivia.IsKind(SyntaxKind.IfDirectiveTrivia) ||
			trivia.IsKind(SyntaxKind.ElseDirectiveTrivia) ||
			trivia.IsKind(SyntaxKind.EndIfDirectiveTrivia) ||
			trivia.IsKind(SyntaxKind.DisabledTextTrivia)
			? default : trivia;
	}
}
