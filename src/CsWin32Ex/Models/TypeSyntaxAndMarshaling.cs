// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

internal struct TypeSyntaxAndMarshaling
{
	internal TypeSyntaxAndMarshaling(TypeSyntax type)
	{
		Type = type;
		MarshalAsAttribute = null;
		NativeArrayInfo = null;
		ParameterModifier = null;
	}

	internal TypeSyntaxAndMarshaling(TypeSyntax type, MarshalAsAttribute? marshalAs, Generator.NativeArrayInfo? nativeArrayInfo)
	{
		Type = type;
		MarshalAsAttribute = marshalAs;
		NativeArrayInfo = nativeArrayInfo;
		ParameterModifier = null;
	}

	internal TypeSyntax Type { get; init; }

	internal MarshalAsAttribute? MarshalAsAttribute { get; init; }

	internal Generator.NativeArrayInfo? NativeArrayInfo { get; }

	internal SyntaxToken? ParameterModifier { get; init; }

	internal FieldDeclarationSyntax AddMarshalAs(FieldDeclarationSyntax fieldDeclaration)
	{
		return MarshalAsAttribute is not null
			? fieldDeclaration.AddAttributeLists(AttributeList().AddAttributes(SimpleSyntaxFactory.MarshalAs(MarshalAsAttribute, NativeArrayInfo)))
			: fieldDeclaration;
	}

	internal ParameterSyntax AddMarshalAs(ParameterSyntax parameter)
	{
		return MarshalAsAttribute is not null
			? parameter.AddAttributeLists(AttributeList().AddAttributes(SimpleSyntaxFactory.MarshalAs(MarshalAsAttribute, NativeArrayInfo)))
			: parameter;
	}

	internal MethodDeclarationSyntax AddReturnMarshalAs(MethodDeclarationSyntax methodDeclaration)
	{
		return MarshalAsAttribute is not null
			? methodDeclaration.AddAttributeLists(AttributeList().WithTarget(AttributeTargetSpecifier(Token(SyntaxKind.ReturnKeyword))).AddAttributes(SimpleSyntaxFactory.MarshalAs(MarshalAsAttribute, NativeArrayInfo)))
			: methodDeclaration;
	}

	internal DelegateDeclarationSyntax AddReturnMarshalAs(DelegateDeclarationSyntax methodDeclaration)
	{
		return MarshalAsAttribute is not null
			? methodDeclaration.AddAttributeLists(AttributeList().WithTarget(AttributeTargetSpecifier(Token(SyntaxKind.ReturnKeyword))).AddAttributes(SimpleSyntaxFactory.MarshalAs(MarshalAsAttribute, NativeArrayInfo)))
			: methodDeclaration;
	}

	internal TypeSyntax GetUnmarshaledType()
	{
		ThrowIfMarshallingRequired();
		return Type;
	}

	internal void ThrowIfMarshallingRequired()
	{
		if (MarshalAsAttribute is not null)
			throw new NotSupportedException("This type requires marshaling, but marshaling is not supported in this context.");
	}
}
