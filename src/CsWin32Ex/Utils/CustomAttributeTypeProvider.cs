// Copyright (c) 0x5BFA.

using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsWin32Ex;

internal class CustomAttributeTypeProvider : ICustomAttributeTypeProvider<TypeSyntax>
{
	internal static readonly CustomAttributeTypeProvider Instance = new();

	private CustomAttributeTypeProvider()
	{
	}

	public TypeSyntax GetPrimitiveType(PrimitiveTypeCode typeCode) => PrimitiveTypeHandleInfo.ToTypeSyntax(typeCode, preferNativeInt: false);

	public TypeSyntax GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
	{
		// CONSIDER: reuse GetNestingQualifiedName (with namespace support added) here.
		TypeReference tr = reader.GetTypeReference(handle);
		string name = reader.GetString(tr.Name);
		string ns = reader.GetString(tr.Namespace);
		return ParseName(ns + "." + name);
	}

	public TypeSyntax GetTypeFromSerializedName(string name) => ParseName(name.IndexOf(',') is int index && index >= 0 ? name.Substring(0, index) : name);

	public PrimitiveTypeCode GetUnderlyingEnumType(TypeSyntax type) => PrimitiveTypeCode.Int32; // an assumption that works for now.

	public bool IsSystemType(TypeSyntax type) => type is QualifiedNameSyntax { Left: IdentifierNameSyntax { Identifier: { ValueText: "System" } }, Right: { Identifier: { ValueText: "Type" } } };

	public TypeSyntax GetSystemType() => throw new NotImplementedException();

	public TypeSyntax GetSZArrayType(TypeSyntax elementType) => throw new NotImplementedException();

	public TypeSyntax GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => throw new NotImplementedException();
}
