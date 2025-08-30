// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

public partial class Generator
{
	/// <inheritdoc/>
	public bool TryGetEnumName(string enumValueName, [NotNullWhen(true)] out string? declaringEnum)
		=> WinMDIndexer.TryGetEnumName(WinMDReader, enumValueName, out declaringEnum);

	private EnumDeclarationSyntax CreateEnumDeclarationSyntax(TypeDefinition typeDefinition)
	{
		// Get if the enum has the "[FlagsAttribute]"
		bool isFlags = FindAttribute(typeDefinition.GetCustomAttributes(), nameof(System), nameof(FlagsAttribute)) is not null;

		List<EnumMemberDeclarationSyntax> enumMemberDeclarationSyntaxCollection = [];
		TryGetEnumBaseType(typeDefinition, out var enumBaseType);

		// Generate the enum
		foreach (var fieldDefHandle in typeDefinition.GetFields())
		{
			if (BuildEnumMemberDeclarationSyntax(fieldDefHandle, enumBaseType, isFlags, out var declarationSyntax))
				enumMemberDeclarationSyntaxCollection.Add(declarationSyntax);
		}

		// Generate the associated constants and put them into the enum as well
		foreach (var associatedConstantAttribute in WinMDFileHelper.FindAttributes(WinMDReader, typeDefinition.GetCustomAttributes(), InteropDecorationNamespace, AssociatedConstantAttribute))
		{
			var decodedAttribute = associatedConstantAttribute.DecodeValue(CustomAttributeTypeProvider.Instance);
			if (decodedAttribute.FixedArguments.Length >= 1 &&
				decodedAttribute.FixedArguments[0].Value is string constName &&
				TryFindConstantByNameInAllNamespaces(constName, out FieldDefinitionHandle fieldDefinitionHandle) &&
				BuildEnumMemberDeclarationSyntax(fieldDefinitionHandle, enumBaseType, isFlags, out var declarationSyntax))
				enumMemberDeclarationSyntaxCollection.Add(declarationSyntax);
		}

		// Build the enum declaration syntax tree
		string? name = WinMDReader.GetString(typeDefinition.Name);
		EnumDeclarationSyntax result = EnumDeclaration(Identifier(name))
			.WithMembers(SyntaxFactory.SeparatedList(enumMemberDeclarationSyntaxCollection, enumMemberDeclarationSyntaxCollection.Select(enumValue => TokenWithLineFeed(SyntaxKind.CommaToken))))
			.WithModifiers(TokenList(TokenWithSpace(Visibility)));

		// If the base type is not "System.Int32" (the default type), explicitly set it in the enum declaration
		if (!(enumBaseType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.IntKeyword }))
			result = result.WithIdentifier(result.Identifier.WithTrailingTrivia(Space))
				.WithBaseList(BaseList(SingletonSeparatedList<BaseTypeSyntax>(SimpleBaseType(enumBaseType).WithTrailingTrivia(LineFeed))).WithColonToken(TokenWithSpace(SyntaxKind.ColonToken)));

		// If the enum is a flags enum, add the "[FlagsAttribute]"
		if (isFlags)
			result = result.AddAttributeLists(AttributeList().WithCloseBracketToken(TokenWithLineFeed(SyntaxKind.CloseBracketToken)).AddAttributes(FlagsAttributeSyntax));

		// Add its documentation if available
		result = AddApiDocumentation(name, result);

		return result;
	}

	private bool TryGetEnumBaseType(TypeDefinition enumDefinition, out TypeSyntax enumBaseType)
	{
		foreach (var enumMemberDefinitionHandle in enumDefinition.GetFields())
		{
			FieldDefinition enumMemberDefinition = WinMDReader.GetFieldDefinition(enumMemberDefinitionHandle);
			string enumMemberName = WinMDReader.GetString(enumMemberDefinition.Name);
			ConstantHandle valueHandle = enumMemberDefinition.GetDefaultValue();

			// Get the type of the enum based on the instance field that does not have a default value,
			// per ECMA-335 "Enums shall have the exactly one instance field which shall be of the underlying type of the enum"
			if (valueHandle.IsNil)
			{
				enumBaseType = enumMemberDefinition.DecodeSignature(SignatureHandleProvider.Instance, null).ToTypeSyntax(_enumTypeSettings, GeneratingElement.EnumValue, null).Type;
				return true;
			}
		}

		// Unexpected and should never happen but just in case
		throw new ArgumentException("Could not find the base type of the enum.");
	}

	private bool BuildEnumMemberDeclarationSyntax(FieldDefinitionHandle enumMemberDefinitionHandle, TypeSyntax enumBaseType, bool isFlags, [NotNullWhen(true)] out EnumMemberDeclarationSyntax? enumMemberDeclarationSyntax)
	{
		enumMemberDeclarationSyntax = null;

		FieldDefinition enumMemberDefinition = WinMDReader.GetFieldDefinition(enumMemberDefinitionHandle);
		string enumMemberName = WinMDReader.GetString(enumMemberDefinition.Name);
		ConstantHandle valueHandle = enumMemberDefinition.GetDefaultValue();
		if (valueHandle.IsNil) return false;

		// Check if the base type is signed integer, to add "unchecked" when the value is actually negative
		bool isEnumBaseTypeSigned = enumBaseType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.LongKeyword or (int)SyntaxKind.IntKeyword or (int)SyntaxKind.ShortKeyword or (int)SyntaxKind.SByteKeyword };

		// If the enum is a flags enum, format the value as 16-based decimal; otherwise, format it as 10-based decimal.
		ExpressionSyntax enumValueExpressionSyntax = isFlags ? ToHexExpressionSyntax(WinMDReader, valueHandle, isEnumBaseTypeSigned) : ToExpressionSyntax(WinMDReader, valueHandle);

		// Build the enum member declaration syntax tree
		enumMemberDeclarationSyntax = EnumMemberDeclaration(SafeIdentifier(enumMemberName))
			.WithEqualsValue(EqualsValueClause(enumValueExpressionSyntax));

		return true;
	}

	private bool TryFindConstantByNameInAllNamespaces(string constName, out FieldDefinitionHandle fieldDefinitionHandle)
	{
		foreach (var ns in WinMDIndexer.MetadataByNamespace)
			if (ns.Value.Fields.TryGetValue(constName, out fieldDefinitionHandle))
				return true;

		fieldDefinitionHandle = default;
		return false;
	}
}
