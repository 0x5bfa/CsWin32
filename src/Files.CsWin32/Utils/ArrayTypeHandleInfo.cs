// Copyright (c) 0x5BFA.

namespace Files.CsWin32;

internal record ArrayTypeHandleInfo(TypeHandleInfo ElementType, ArrayShape Shape) : TypeHandleInfo, ITypeHandleContainer
{
	public override string ToString() => this.ToTypeSyntaxForDisplay().ToString();

	internal override TypeSyntaxAndMarshaling ToTypeSyntax(TypeSyntaxSettings inputs, GeneratingElement forElement, CustomAttributeHandleCollection? customAttributes, ParameterAttributes parameterAttributes)
	{
		TypeSyntaxAndMarshaling element = this.ElementType.ToTypeSyntax(inputs, forElement, customAttributes);
		if (inputs.AllowMarshaling || inputs.IsField)
		{
			ArrayTypeSyntax arrayType = ArrayType(element.Type, SingletonList(ArrayRankSpecifier().AddSizes(this.Shape.Sizes.Select(size => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(size))).ToArray<ExpressionSyntax>())));
			MarshalAsAttribute? marshalAs = element.MarshalAsAttribute is object ? new MarshalAsAttribute(UnmanagedType.LPArray) { ArraySubType = element.MarshalAsAttribute.Value } : null;
			return new TypeSyntaxAndMarshaling(arrayType, marshalAs, element.NativeArrayInfo);
		}
		else
		{
			return new TypeSyntaxAndMarshaling(PointerType(element.Type));
		}
	}

	internal override bool? IsValueType(TypeSyntaxSettings inputs) => false;
}
