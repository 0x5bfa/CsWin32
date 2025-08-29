// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

public partial class Generator
{
	/// <inheritdoc/>
	public bool TryGetEnumName(string enumValueName, [NotNullWhen(true)] out string? declaringEnum) => this.WinMDIndexer.TryGetEnumName(this.WinMDReader, enumValueName, out declaringEnum);

	private EnumDeclarationSyntax DeclareEnum(TypeDefinition typeDef)
	{
		bool flagsEnum = this.FindAttribute(typeDef.GetCustomAttributes(), nameof(System), nameof(FlagsAttribute)) is not null;

		var enumValues = new List<SyntaxNodeOrToken>();
		TypeSyntax? enumBaseType = null;
		foreach (FieldDefinitionHandle fieldDefHandle in typeDef.GetFields())
		{
			AddEnumValue(fieldDefHandle);
		}

		// Add associated constants.
		foreach (CustomAttribute associatedConstAtt in WinMDFileHelper.FindAttributes(this.WinMDReader, typeDef.GetCustomAttributes(), InteropDecorationNamespace, AssociatedConstantAttribute))
		{
			CustomAttributeValue<TypeSyntax> decodedAttribute = associatedConstAtt.DecodeValue(CustomAttributeTypeProvider.Instance);
			if (decodedAttribute.FixedArguments.Length >= 1 && decodedAttribute.FixedArguments[0].Value is string constName)
			{
				if (TryFindConstant(constName, out FieldDefinitionHandle fieldHandle))
				{
					AddEnumValue(fieldHandle);
				}
			}
		}

		if (enumBaseType is null)
		{
			throw new NotSupportedException("Unknown enum type.");
		}

		string? name = this.WinMDReader.GetString(typeDef.Name);
		EnumDeclarationSyntax result = EnumDeclaration(Identifier(name))
			.WithMembers(SeparatedList<EnumMemberDeclarationSyntax>(enumValues))
			.WithModifiers(TokenList(TokenWithSpace(this.Visibility)));

		if (!(enumBaseType is PredefinedTypeSyntax { Keyword: { RawKind: (int)SyntaxKind.IntKeyword } }))
		{
			result = result.WithIdentifier(result.Identifier.WithTrailingTrivia(Space))
				.WithBaseList(BaseList(SingletonSeparatedList<BaseTypeSyntax>(SimpleBaseType(enumBaseType).WithTrailingTrivia(LineFeed))).WithColonToken(TokenWithSpace(SyntaxKind.ColonToken)));
		}

		if (flagsEnum)
		{
			result = result.AddAttributeLists(
				AttributeList().WithCloseBracketToken(TokenWithLineFeed(SyntaxKind.CloseBracketToken)).AddAttributes(FlagsAttributeSyntax));
		}

		result = this.AddApiDocumentation(name, result);

		return result;

		void AddEnumValue(FieldDefinitionHandle fieldDefHandle)
		{
			FieldDefinition fieldDef = this.WinMDReader.GetFieldDefinition(fieldDefHandle);
			string enumValueName = this.WinMDReader.GetString(fieldDef.Name);
			ConstantHandle valueHandle = fieldDef.GetDefaultValue();

			// Enums shall have the exactly one instance field which shall be of the underlying type of the enum per ECMA-335
			if (valueHandle.IsNil)
			{
				enumBaseType = fieldDef.DecodeSignature(SignatureHandleProvider.Instance, null).ToTypeSyntax(this._enumTypeSettings, GeneratingElement.EnumValue, null).Type;
				return;
			}

			bool enumBaseTypeIsSigned = enumBaseType is PredefinedTypeSyntax { Keyword: { RawKind: (int)SyntaxKind.LongKeyword or (int)SyntaxKind.IntKeyword or (int)SyntaxKind.ShortKeyword or (int)SyntaxKind.SByteKeyword } };
			ExpressionSyntax enumValue = flagsEnum ? ToHexExpressionSyntax(this.WinMDReader, valueHandle, enumBaseTypeIsSigned) : ToExpressionSyntax(this.WinMDReader, valueHandle);
			EnumMemberDeclarationSyntax enumMember = EnumMemberDeclaration(SafeIdentifier(enumValueName))
				.WithEqualsValue(EqualsValueClause(enumValue));
			enumValues.Add(enumMember);
			enumValues.Add(TokenWithLineFeed(SyntaxKind.CommaToken));
		}

		bool TryFindConstant(string name, out FieldDefinitionHandle fieldDefinitionHandle)
		{
			foreach (var ns in this.WinMDIndexer.MetadataByNamespace)
			{
				if (ns.Value.Fields.TryGetValue(name, out fieldDefinitionHandle))
				{
					return true;
				}
			}

			fieldDefinitionHandle = default;
			return false;
		}
	}
}
