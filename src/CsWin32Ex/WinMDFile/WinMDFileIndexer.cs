// Copyright (c) 0x5BFA.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection.PortableExecutable;

namespace CsWin32Ex;

/// <summary>A cached, shareable indexer for a particular metadata file.</summary>
/// <remarks>This class <em>must not</em> store anything to do with a <see cref="MetadataReader"/>, since that is attached to a stream which will not allow for concurrent use. This means we cannot store definitions (e.g. <see cref="TypeDefinition"/>) because they store the <see cref="MetadataReader"/> as a field. We can store handles though (e.g. <see cref="TypeDefinitionHandle"/>, since the only thing they store is an index into the metadata, which is constant across <see cref="MetadataReader"/> instances for a given metadata file.</remarks>
[DebuggerDisplay($"{{{nameof(_winMDFile.Path)} ({nameof(_buildPlatform)}),nq}}")]
internal class WinMDFileIndexer
{
	private readonly WinMDFile _winMDFile;
	private readonly Platform? _buildPlatform;
	private readonly List<TypeDefinitionHandle> _apis = [];
	private readonly HashSet<string> _releaseMethods = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<TypeReferenceHandle, TypeDefinitionHandle> _refToDefCache = new();
	/// <summary>The set of names of typedef structs that represent handles where the handle has length of <see cref="IntPtr"/>and is therefore appropriate to wrap in a <see cref="SafeHandle"/>.</summary>
	private readonly HashSet<string> _handleTypeStructsWithIntPtrSizeFields = new(StringComparer.Ordinal);
	/// <summary>A dictionary where the key is the typedef struct name and the value is the method used to release it.</summary>
	private readonly Dictionary<TypeDefinitionHandle, string> _handleTypeReleaseMethod = [];
	/// <summary>A cache kept by the <see cref="TryGetEnumName"/> method.</summary>
	private readonly ConcurrentDictionary<string, string?> _enumValueLookupCache = new(StringComparer.Ordinal);

	/// <summary>A lazily computed reference to System.Enum, as defined by this metadata. Should be retrieved by <see cref="FindEnumTypeReference(MetadataReader)"/>.</summary>
	private TypeReferenceHandle? _enumTypeReference;

	/// <summary>Gets the assembly name of the metadata file.</summary>
	internal string WinMDAssemblyName { get; }

	/// <summary>Gets the ref handle to the constructor on the SupportedArchitectureAttribute, if there is one.</summary>
	internal MemberReferenceHandle SupportedArchitectureAttributeCtor { get; }

	/// <summary>Gets the "Apis" classes across all namespaces.</summary>
	internal ReadOnlyCollection<TypeDefinitionHandle> Apis => new(_apis);

	/// <summary>Gets a dictionary of namespace metadata, indexed by the string handle to their namespace.</summary>
	internal Dictionary<string, NamespaceMetadata> MetadataByNamespace { get; } = [];

	internal IReadOnlyCollection<string> ReleaseMethods
		=> _releaseMethods;

	internal IReadOnlyDictionary<TypeDefinitionHandle, string> HandleTypeReleaseMethod
		=> _handleTypeReleaseMethod;

	internal string CommonNamespace { get; }

	internal string CommonNamespaceWithDot { get; }

	/// <summary>Initializes an instance of <see cref="WinMDFileIndexer"/>.</summary>
	/// <param name="winMDFile">The metadata file that this index will represent.</param>
	/// <param name="buildPlatform">The platform filter to apply when reading the metadata.</param>
	internal WinMDFileIndexer(WinMDFile winMDFile, Platform? buildPlatform)
	{
		_winMDFile = winMDFile;
		_buildPlatform = buildPlatform;

		// Get a metadata reader for the WinMD file.
		using WinMDReaderRental rental = winMDFile.RentWinMDReader();
		MetadataReader reader = rental.Value;
		WinMDAssemblyName = Path.GetFileNameWithoutExtension(reader.GetString(reader.GetAssemblyDefinition().Name));

		// [ONLY for win32metadata] Find "SupportedArchitectureAttribute" from the metadata and save it to produce an error when a type that isn't platform-compatible is requested.
		foreach (var memberRefHandle in reader.MemberReferences)
		{
			var memberReference = reader.GetMemberReference(memberRefHandle);
			if (memberReference.GetKind() is MemberReferenceKind.Method)
			{
				if (memberReference.Parent.Kind is HandleKind.TypeReference)
				{
					if (reader.StringComparer.Equals(memberReference.Name, ".ctor"))
					{
						var typeReferenceHandle = (TypeReferenceHandle)memberReference.Parent;
						TypeReference typeReference = reader.GetTypeReference(typeReferenceHandle);
						if (reader.StringComparer.Equals(typeReference.Name, "SupportedArchitectureAttribute") &&
							reader.StringComparer.Equals(typeReference.Namespace, Generator.InteropDecorationNamespace))
						{
							SupportedArchitectureAttributeCtor = memberRefHandle;
							break;
						}
					}
				}
			}
		}

		// Get and cache the all namespaces in the metadata.
		foreach (var childNamespaceHandle in reader.GetNamespaceDefinitionRoot().NamespaceDefinitions)
			PopulateNamespace(reader, reader.GetNamespaceDefinitionRoot(), parentNamespace: null);

		// Find the prefix the namespaces in the metadata have in common.
		CommonNamespace = GetCommonNamespacePrefix([.. MetadataByNamespace.Keys]);
		if (CommonNamespace[^1] == '.') CommonNamespace = CommonNamespace[..^1];

		CommonNamespaceWithDot = CommonNamespace + ".";
	}

	/// <summary>
	/// Attempts to translate a <see cref="TypeReferenceHandle"/> to a <see cref="TypeDefinitionHandle"/>.
	/// </summary>
	/// <param name="reader">The metadata reader to use.</param>
	/// <param name="typeRefHandle">The reference handle.</param>
	/// <param name="typeDefHandle">Receives the type def handle, if one was discovered.</param>
	/// <returns><see langword="true"/> if a TypeDefinition was found; otherwise <see langword="false"/>.</returns>
	internal bool TryGetTypeDefHandle(MetadataReader reader, TypeReferenceHandle typeRefHandle, out TypeDefinitionHandle typeDefHandle)
	{
		if (_refToDefCache.TryGetValue(typeRefHandle, out typeDefHandle))
		{
			return !typeDefHandle.IsNil;
		}

		TypeReference typeRef = reader.GetTypeReference(typeRefHandle);
		if (typeRef.ResolutionScope.Kind != HandleKind.AssemblyReference)
		{
			TypeDefinitionHandle expectedNestingTypeDef = default;
			bool foundNestingTypeDef = false;
			if (typeRef.ResolutionScope.Kind == HandleKind.TypeReference)
			{
				foundNestingTypeDef = this.TryGetTypeDefHandle(reader, (TypeReferenceHandle)typeRef.ResolutionScope, out expectedNestingTypeDef);
			}

			bool foundPlatformIncompatibleMatch = false;
			foreach (TypeDefinitionHandle tdh in reader.TypeDefinitions)
			{
				TypeDefinition typeDef = reader.GetTypeDefinition(tdh);
				if (typeDef.Name == typeRef.Name && typeDef.Namespace == typeRef.Namespace)
				{
					if (!WinMDFileHelper.IsCompatibleWithPlatform(reader, this, _buildPlatform, typeDef.GetCustomAttributes()))
					{
						foundPlatformIncompatibleMatch = true;
						continue;
					}

					if (typeRef.ResolutionScope.Kind == HandleKind.TypeReference)
					{
						// The ref is nested. Verify that the type we found is nested in the same type as well.
						TypeDefinitionHandle actualNestingType = typeDef.GetDeclaringType();
						if (foundNestingTypeDef && expectedNestingTypeDef == actualNestingType)
						{
							typeDefHandle = tdh;
							break;
						}
					}
					else if (typeRef.ResolutionScope.Kind == HandleKind.ModuleDefinition && typeDef.GetDeclaringType().IsNil)
					{
						typeDefHandle = tdh;
						break;
					}
					else
					{
						throw new NotSupportedException("Unrecognized ResolutionScope: " + typeRef.ResolutionScope);
					}
				}
			}

			if (foundPlatformIncompatibleMatch && typeDefHandle.IsNil)
			{
				string ns = reader.GetString(typeRef.Namespace);
				string name = reader.GetString(typeRef.Name);
				throw new PlatformIncompatibleException($"{ns}.{name} is not declared for this platform.");
			}
		}

		_refToDefCache.TryAdd(typeRefHandle, typeDefHandle);
		return !typeDefHandle.IsNil;
	}

	/// <summary>
	/// Gets the name of the declaring enum if a supplied value matches the name of an enum's value.
	/// </summary>
	/// <param name="reader">A metadata reader that can be used to fulfill this query.</param>
	/// <param name="enumValueName">A string that may match an enum value name.</param>
	/// <param name="declaringEnum">Receives the name of the declaring enum if a match is found.</param>
	/// <returns><see langword="true"/> if a match was found; otherwise <see langword="false"/>.</returns>
	internal bool TryGetEnumName(MetadataReader reader, string enumValueName, [NotNullWhen(true)] out string? declaringEnum)
	{
		if (_enumValueLookupCache.TryGetValue(enumValueName, out declaringEnum))
		{
			return declaringEnum is not null;
		}

		// First find the type reference for System.Enum
		TypeReferenceHandle? enumTypeRefHandle = FindEnumTypeReference(reader);
		if (enumTypeRefHandle is null)
		{
			// No enums -> it couldn't be what the caller is looking for.
			// This is a quick enough check that we don't need to cache individual inputs/outputs when nothing will produce results for this metadata.
			declaringEnum = null;
			return false;
		}

		foreach (TypeDefinitionHandle typeDefHandle in reader.TypeDefinitions)
		{
			TypeDefinition typeDef = reader.GetTypeDefinition(typeDefHandle);
			if (typeDef.BaseType.IsNil)
			{
				continue;
			}

			if (typeDef.BaseType.Kind != HandleKind.TypeReference)
			{
				continue;
			}

			var baseTypeHandle = (TypeReferenceHandle)typeDef.BaseType;
			if (!baseTypeHandle.Equals(enumTypeRefHandle.Value))
			{
				continue;
			}

			foreach (FieldDefinitionHandle fieldDefHandle in typeDef.GetFields())
			{
				FieldDefinition fieldDef = reader.GetFieldDefinition(fieldDefHandle);
				if (reader.StringComparer.Equals(fieldDef.Name, enumValueName))
				{
					declaringEnum = reader.GetString(typeDef.Name);

					_enumValueLookupCache[enumValueName] = declaringEnum;

					return true;
				}
			}
		}

		_enumValueLookupCache[enumValueName] = null;
		declaringEnum = null;
		return false;
	}

	private void PopulateNamespace(MetadataReader reader, NamespaceDefinition namespaceDefinition, string? parentNamespace)
	{
		string namespaceLeafName = reader.GetString(namespaceDefinition.Name);
		string namespaceFullName = string.IsNullOrEmpty(parentNamespace) ? namespaceLeafName : $"{parentNamespace}.{namespaceLeafName}";

		var namespaceMetadata = new NamespaceMetadata(namespaceFullName);

		foreach (TypeDefinitionHandle typeDefinitionHandle in namespaceDefinition.TypeDefinitions)
		{
			TypeDefinition typeDefinition = reader.GetTypeDefinition(typeDefinitionHandle);
			string typeName = reader.GetString(typeDefinition.Name);

			if (typeName == "Apis")
			{
				_apis.Add(typeDefinitionHandle);

				foreach (MethodDefinitionHandle methodDefinitionHandle in typeDefinition.GetMethods())
				{
					MethodDefinition methodDefinition = reader.GetMethodDefinition(methodDefinitionHandle);
					string methodName = reader.GetString(methodDefinition.Name);

					if (WinMDFileHelper.IsCompatibleWithPlatform(reader, this, _buildPlatform, methodDefinition.GetCustomAttributes()))
						namespaceMetadata.Methods.Add(methodName, methodDefinitionHandle);
					else
						namespaceMetadata.MethodsForOtherPlatform.Add(methodName);
				}

				foreach (FieldDefinitionHandle fieldDefinitionHandle in typeDefinition.GetFields())
				{
					FieldDefinition fieldDefinition = reader.GetFieldDefinition(fieldDefinitionHandle);
					const FieldAttributes expectedFlags = FieldAttributes.Static | FieldAttributes.Public;
					if ((fieldDefinition.Attributes & expectedFlags) == expectedFlags)
					{
						string fieldName = reader.GetString(fieldDefinition.Name);
						namespaceMetadata.Fields.Add(fieldName, fieldDefinitionHandle);
					}
				}
			}
			else if (typeName == "<Module>")
			{
			}
			else if (WinMDFileHelper.IsCompatibleWithPlatform(reader, this, _buildPlatform, typeDefinition.GetCustomAttributes()))
			{
				namespaceMetadata.Types.Add(typeName, typeDefinitionHandle);

				// Check if this is a struct representing a native handle
				if (typeDefinition.GetFields().Count is 1 && typeDefinition.BaseType.Kind is HandleKind.TypeReference)
				{
					TypeReference baseType = reader.GetTypeReference((TypeReferenceHandle)typeDefinition.BaseType);

					// Check if this type is a struct derived from "System.ValueType"
					if (reader.StringComparer.Equals(baseType.Name, nameof(ValueType)) && reader.StringComparer.Equals(baseType.Namespace, nameof(System)))
					{
						// Check if this has "RAIIFreeAttribute"
						if (WinMDFileHelper.TryGetAttributeOn(reader, typeDefinition.GetCustomAttributes(), Generator.InteropDecorationNamespace, Generator.RAIIFreeAttribute) is CustomAttribute att)
						{
							CustomAttributeValue<TypeSyntax> args = att.DecodeValue(CustomAttributeTypeProvider.Instance);
							if (args.FixedArguments[0].Value is string freeMethodName)
							{
								_handleTypeReleaseMethod.Add(typeDefinitionHandle, freeMethodName);
								_releaseMethods.Add(freeMethodName);

								// Get the field that represents the native handle
								using FieldDefinitionHandleCollection.Enumerator fieldEnum = typeDefinition.GetFields().GetEnumerator();
								fieldEnum.MoveNext();
								FieldDefinitionHandle fieldHandle = fieldEnum.Current;
								FieldDefinition fieldDef = reader.GetFieldDefinition(fieldHandle);

								// Check if the field that represents the native handle is the type that can be wrapped in a SafeHandle (i.e. IntPtr or UIntPtr)
								if (fieldDef.DecodeSignature(SignatureHandleProvider.Instance, null) is PrimitiveTypeHandleInfo { PrimitiveTypeCode: PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr })
									_handleTypeStructsWithIntPtrSizeFields.Add(typeName);
							}
						}
					}
				}
			}
			else
			{
				namespaceMetadata.TypesForOtherPlatform.Add(typeName);
			}
		}

		if (!namespaceMetadata.IsEmpty)
			MetadataByNamespace.Add(namespaceFullName, namespaceMetadata);

		// Recurse into child namespaces
		foreach (NamespaceDefinitionHandle childNsHandle in namespaceDefinition.NamespaceDefinitions)
			PopulateNamespace(reader, reader.GetNamespaceDefinition(childNsHandle), namespaceFullName);
	}

	private static string GetCommonNamespacePrefix(IReadOnlyList<string> namespaces)
	{
		if (namespaces.Count is 0)
			return string.Empty;
		if (namespaces.Count is 1)
			return namespaces[0];

		int prefixLength = 0;

		foreach (char c in namespaces[0])
		{
			foreach (string s in namespaces)
				if (s.Length <= prefixLength || s[prefixLength] != c)
					return namespaces[0][..prefixLength];

			prefixLength++;
		}

		return namespaces[0]; // all strings identical up to length of namespaces[0]
	}

	/// <summary>
	/// Gets the <see cref="TypeReferenceHandle"/> by which the <see cref="Enum"/> class in referenced by this metadata.
	/// </summary>
	/// <param name="reader">The reader to use.</param>
	/// <returns>The <see cref="TypeReferenceHandle"/> if a reference to <see cref="Enum"/> was found; otherwise <see langword="null" />.</returns>
	private TypeReferenceHandle? FindEnumTypeReference(MetadataReader reader)
	{
		if (!_enumTypeReference.HasValue)
		{
			foreach (TypeReferenceHandle typeRefHandle in reader.TypeReferences)
			{
				TypeReference typeRef = reader.GetTypeReference(typeRefHandle);
				if (reader.StringComparer.Equals(typeRef.Name, nameof(Enum)) && reader.StringComparer.Equals(typeRef.Namespace, nameof(System)))
				{
					_enumTypeReference = typeRefHandle;
					break;
				}
			}

			if (!_enumTypeReference.HasValue)
			{
				// Record that there isn't one.
				_enumTypeReference = default(TypeReferenceHandle);
			}
		}

		// Return null if the value was determined to be missing.
		return _enumTypeReference.HasValue && !_enumTypeReference.Value.IsNil ? _enumTypeReference.Value : null;
	}
}
