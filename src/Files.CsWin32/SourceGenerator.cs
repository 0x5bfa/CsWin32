// Copyright (c) 0x5BFA.

using Microsoft.CodeAnalysis.Text;
using System.Text.Json;

namespace Files.CsWin32;

/// <summary>
/// Generates the source code for the p/invoke methods and supporting types into some C# project.
/// </summary>
[Generator()]
public class SourceGenerator : ISourceGenerator
{
	private const string NativeMethodsTxtAdditionalFileName = "NativeMethods.txt";
	private const string NativeMethodsJsonAdditionalFileName = "NativeMethods.json";

	private static readonly char[] ZeroWhiteSpace =
	[
		'\uFEFF', // ZERO WIDTH NO-BREAK SPACE (U+FEFF)
		'\u200B', // ZERO WIDTH SPACE (U+200B)
	];

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	/// <inheritdoc/>
	public void Initialize(GeneratorInitializationContext context) { }

	/// <inheritdoc/>
	public void Execute(GeneratorExecutionContext context)
	{
		if (context.Compilation is not CSharpCompilation compilation ||
			GetNativeMethodsJsonOptions(context) is not { } options ||
			GetNativeMethodsTxtFiles(context) is not { } nativeMethodsTxtFiles ||
			!nativeMethodsTxtFiles.Any())
			return;

		// Produce some diagnostics before actually starting to generate
		if (!compilation.Options.AllowUnsafe)
			context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.UnsafeCodeRequired, location: null));
		if (compilation.GetTypeByMetadataName("System.Memory`1") is null)
			context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MissingRecommendedReference, location: null, "System.Memory"));

		// Create a generator for each WinMD file
		var generators = CreateGeneratorsForEachWinMDFiles(context, compilation, options);
		if (generators is null)
			return;

		// Generate the syntax trees for all requested APIs
		using GeneratorManager superGenerator = GeneratorManager.CreateFromGenerators(generators.AsEnumerable());
		foreach (AdditionalText nativeMethodsTxtFile in nativeMethodsTxtFiles)
			GenerateAll(context, superGenerator, nativeMethodsTxtFile, options);

		// Produce C# source files from the accumulated compilation units.
		foreach (KeyValuePair<string, CompilationUnitSyntax> unit in superGenerator.GetCompilationUnits(context.CancellationToken))
			context.AddSource(unit.Key, unit.Value.GetText(Encoding.UTF8));
	}

	private static GeneratorOptions? GetNativeMethodsJsonOptions(GeneratorExecutionContext context)
	{
		GeneratorOptions options;
		AdditionalText? nativeMethodsJsonFile = context.AdditionalFiles
			.FirstOrDefault(af => string.Equals(Path.GetFileName(af.Path), NativeMethodsJsonAdditionalFileName, StringComparison.OrdinalIgnoreCase));

		if (nativeMethodsJsonFile is not null)
		{
			string optionsJson = nativeMethodsJsonFile.GetText(context.CancellationToken)!.ToString();

			try
			{
				options = JsonSerializer.Deserialize<GeneratorOptions>(optionsJson, JsonOptions);
			}
			catch (JsonException ex)
			{
				context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.OptionsParsingError, location: null, nativeMethodsJsonFile.Path, ex.Message));
				return null;
			}
		}
		else
		{
			options = new GeneratorOptions();
		}

		return options;
	}

	private static IEnumerable<AdditionalText> GetNativeMethodsTxtFiles(GeneratorExecutionContext context)
	{
		IEnumerable<AdditionalText> nativeMethodsTxtFiles = context.AdditionalFiles
			.Where(af => string.Equals(Path.GetFileName(af.Path), NativeMethodsTxtAdditionalFileName, StringComparison.OrdinalIgnoreCase));
		return nativeMethodsTxtFiles;
	}

	private static IEnumerable<Generator>? CreateGeneratorsForEachWinMDFiles(GeneratorExecutionContext context, CSharpCompilation compilation, GeneratorOptions options)
	{
		IEnumerable<string> appLocalLibraries = CollectAppLocalAllowedLibraries(context);
		Docs? docs = ParseDocs(context);
		var generators = GetAllWinMdFilePaths(context).Select(path => new Generator(path, docs, appLocalLibraries, options, compilation, (CSharpParseOptions)context.ParseOptions));
		if (TryFindNonUniqueValue(generators, g => g.InputAssemblyName, StringComparer.OrdinalIgnoreCase, out (Generator Item, string Value) nonUniqueGenerator))
		{
			context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NonUniqueMetadataInputs, null, nonUniqueGenerator.Value));
			return null;
		}

		return generators;
	}

	private static string AssembleFullExceptionMessage(Exception ex)
	{
		var sb = new StringBuilder();

		Exception? inner = ex;
		while (inner is object)
		{
			sb.Append(inner.Message);
			if (sb.Length > 0 && sb[sb.Length - 1] != '.')
			{
				sb.Append('.');
			}

			sb.Append(' ');
			inner = inner.InnerException;
		}

		sb.AppendLine();
		sb.AppendLine(ex.ToString());

		return sb.ToString();
	}

	private static bool TryFindNonUniqueValue<T, TValue>(IEnumerable<T> sequence, Func<T, TValue> valueSelector, IEqualityComparer<TValue> comparer, out (T Item, TValue Value) nonUniqueValue)
	{
		HashSet<TValue> seenValues = new(comparer);
		nonUniqueValue = default;
		foreach (T item in sequence)
		{
			TValue value = valueSelector(item);
			if (!seenValues.Add(value))
			{
				nonUniqueValue = (item, value);
				return true;
			}
		}

		return false;
	}

	private static IReadOnlyList<string> GetAllWinMdFilePaths(GeneratorExecutionContext context)
	{
		if (!context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.CsWin32InputMetadataPaths", out string? delimitedMetadataBasePaths) ||
			string.IsNullOrWhiteSpace(delimitedMetadataBasePaths))
		{
			return [];
		}

		string[] metadataBasePaths = delimitedMetadataBasePaths.Split('|');
		return metadataBasePaths;
	}

	private static IEnumerable<string> CollectAppLocalAllowedLibraries(GeneratorExecutionContext context)
	{
		if (!context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.CsWin32AppLocalAllowedLibraries", out string? delimitedAppLocalLibraryPaths) ||
			string.IsNullOrWhiteSpace(delimitedAppLocalLibraryPaths))
		{
			return Array.Empty<string>();
		}

		return delimitedAppLocalLibraryPaths.Split('|').Select(Path.GetFileName);
	}

	private static void GenerateAll(GeneratorExecutionContext context, GeneratorManager manager, AdditionalText nativeMethodsTxtFile, GeneratorOptions options)
	{
		SourceText? nativeMethodsTxt = nativeMethodsTxtFile.GetText(context.CancellationToken);
		if (nativeMethodsTxt is null)
			return;

		// Go through each line of the file and generate it.
		foreach (TextLine line in nativeMethodsTxt.Lines)
		{
			context.CancellationToken.ThrowIfCancellationRequested();

			string name = line.ToString();

			// Skip blank lines and comment lines.
			if (string.IsNullOrWhiteSpace(name) || name.StartsWith("//", StringComparison.InvariantCulture))
				continue;

			// Remove ordinal whitespaces and ZWSP characters.
			name = name.Trim().Trim(ZeroWhiteSpace);

			// Get the location of the line for diagnostics.
			var location = Location.Create(nativeMethodsTxtFile.Path, line.Span, nativeMethodsTxt.Lines.GetLinePositionSpan(line.Span));

			try
			{
				// If the API requested is deprecated or not recommended, report that and move on.
				if (Generator.GetBannedAPIs(options).TryGetValue(name, out string? reason))
				{
					context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.BannedApi, location, reason));
					continue;
				}

				// If the name ends with "<module name>.*", generate all methods from the module (e.g., "Shell32.*").
				if (name.EndsWith(".*", StringComparison.Ordinal))
				{
					string? moduleName = name[..^2];
					int matches = manager.TryGenerateAllExternMethods(moduleName, context.CancellationToken);
					switch (matches)
					{
						case 0:
							context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NoMethodsForModule, location, moduleName));
							break;
						case > 1:
							// Stop complaining about multiple metadata exporting methods from the same module.
							// https://github.com/microsoft/CsWin32/issues/1201
							////context.ReportDiagnostic(Diagnostic.Create(AmbiguousMatchError, location, moduleName));
							break;
					}

					continue;
				}

				// Now, the name should be a method or type name. Generate it.
				manager.TryGenerate(name, out IReadOnlyCollection<string> matchingApis, out IReadOnlyCollection<string> redirectedEnums, context.CancellationToken);
				foreach (string declaringEnum in redirectedEnums) context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.UseEnumValueDeclaringType, location, declaringEnum));
				switch (matchingApis.Count)
				{
					case 0:
						IReadOnlyList<string> suggestions = manager.GetSuggestions(name);
						switch (suggestions.Count)
						{
							case > 0:
								context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NoMatchingMethodOrTypeWithSuggestions, location, name, CombineDiagnosticSuggestions(suggestions)));
								break;
							default:
								context.ReportDiagnostic(Diagnostic.Create(Generator.ContainsIllegalCharactersForAPIName(name) ? DiagnosticDescriptors.NoMatchingMethodOrTypeWithBadCharacters : DiagnosticDescriptors.NoMatchingMethodOrType, location, name));
								break;
						}
						break;
					case > 1:
						context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.AmbiguousMatchErrorWithSuggestions, location, name, CombineDiagnosticSuggestions(matchingApis)));
						break;
				}
			}
			catch (GenerationFailedException ex)
			{
				if (Generator.IsPlatformCompatibleException(ex))
				{
					context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CpuArchitectureIncompatibility, location));
				}
				else
				{
					// Build up a complete error message.
					context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InternalError, location, AssembleFullExceptionMessage(ex)));
				}
			}
		}
	}

	private static Docs? ParseDocs(GeneratorExecutionContext context)
	{
		Docs? docs = null;
		if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.CsWin32InputDocPaths", out string? delimitedApiDocsPaths) &&
			!string.IsNullOrWhiteSpace(delimitedApiDocsPaths))
		{
			string[] apiDocsPaths = delimitedApiDocsPaths!.Split('|');
			if (apiDocsPaths.Length > 0)
			{
				List<Docs> docsList = new(apiDocsPaths.Length);
				foreach (string path in apiDocsPaths)
				{
					try
					{
						docsList.Add(Docs.Get(path));
					}
					catch (Exception e)
					{
						context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DocParsingError, null, path, e));
					}
				}

				docs = Docs.Merge(docsList);
			}
		}

		return docs;
	}

	private static string CombineDiagnosticSuggestions(IReadOnlyCollection<string> suggestions)
	{
		var suggestionBuilder = new StringBuilder();
		int i = 0;
		foreach (string suggestion in suggestions)
		{
			if (++i > 0)
				suggestionBuilder.Append(i < suggestions.Count - 1 ? ", " : " or ");

			suggestionBuilder.Append('"');
			suggestionBuilder.Append(suggestion);
			suggestionBuilder.Append('"');
		}

		return suggestionBuilder.ToString();
	}
}
