// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

public partial class Generator
{
	internal static bool IsUntypedDelegate(MetadataReader reader, TypeDefinition typeDef)
		=> reader.StringComparer.Equals(typeDef.Name, "PROC") || reader.StringComparer.Equals(typeDef.Name, "FARPROC");

	/// <summary>Creates a delegate declaration syntax from a <see cref="TypeDefinition"/>.</summary>
	/// <remarks>Creating a delegate when <see cref="GeneratorOptions.AllowMarshaling"/> is <see langword="true"/>.</remarks>
	/// <param name="typeDefinition">The type to create a delegate syntax tree from.</param>
	/// <returns>An instance of <see cref="DelegateDeclarationSyntax"/> that represents a delegate.</returns>
	private DelegateDeclarationSyntax CreateTypedDelegateDeclaration(TypeDefinition typeDefinition)
	{
		// Not expected but just in case
		if (!_options.AllowMarshaling)
			throw new NotSupportedException("Delegates will not be generated when the runtime marshalling is disabled.");

		var delegateName = WinMDReader.GetString(typeDefinition.Name);
		var typeSettings = _delegateSignatureTypeSettings;
		CallingConvention? callingConvention = null;

		// Check for "[UnmanagedFunctionPointerAttribute]" to get the calling convention
		if (FindAttribute(typeDefinition.GetCustomAttributes(), SystemRuntimeInteropServices, nameof(UnmanagedFunctionPointerAttribute)) is CustomAttribute attribute)
		{
			CustomAttributeValue<TypeSyntax> args = attribute.DecodeValue(CustomAttributeTypeProvider.Instance);
			callingConvention = (CallingConvention)(int)args.FixedArguments[0].Value!;
		}

		// Get the parameters and return type of the delegate
		GetSignatureOfDelegate(typeDefinition, out MethodDefinition invokeMethodDef, out MethodSignature<TypeHandleInfo> signature, out CustomAttributeHandleCollection? returnTypeAttributes);
		var returnValueType = signature.ReturnType.ToTypeSyntax(typeSettings, GeneratingElement.Delegate, returnTypeAttributes);

		// Build the delegate declaration syntax tree
		DelegateDeclarationSyntax syntax = DelegateDeclaration(returnValueType.Type, Identifier(delegateName))
			.WithParameterList(FixTrivia(CreateParameterList(invokeMethodDef, signature, typeSettings, GeneratingElement.Delegate)))
			.AddModifiers(TokenWithSpace(Visibility), TokenWithSpace(SyntaxKind.UnsafeKeyword));

		// Add the [MarshalAsAttribute] on the return type, if needed
		syntax = returnValueType.AddReturnMarshalAs(syntax);

		// Add the calling convention attribute if we found one
		if (callingConvention.HasValue)
			syntax = syntax.AddAttributeLists(AttributeList().AddAttributes(UnmanagedFunctionPointer(callingConvention.Value)).WithCloseBracketToken(TokenWithLineFeed(SyntaxKind.CloseBracketToken)));

		return syntax;
	}

	/// <summary>Creates a struct from an untyped delegate utilizing <see cref="Marshal.GetDelegateForFunctionPointer{TDelegate}(IntPtr)"/>.</summary>
	/// <param name="typeDefinition">The type to create a struct syntax tree with the marshalled delegate from.</param>
	/// <returns>An instance of <see cref="StructDeclarationSyntax"/> that represents a struct.</returns>
	private StructDeclarationSyntax CreateUntypedDelegateDeclaration(TypeDefinition typeDefinition)
	{
		var typeName = IdentifierName(WinMDReader.GetString(typeDefinition.Name));
		var valueFieldName = IdentifierName("Value");

		// Generate "internal IntPtr Value;"
		var fieldDeclaration = FieldDeclaration(VariableDeclaration(IntPtrTypeSyntax.WithTrailingTrivia(TriviaList(Space)))
			.AddVariables(VariableDeclarator(valueFieldName.Identifier))).AddModifiers(TokenWithSpace(Visibility));

		// Generate "Marshal.GetDelegateForFunctionPointer<TDelegate>()" or "Marshal.GetDelegateForFunctionPointer()"
		var genericTypeParameter = IdentifierName("TDelegate");
		var methodCallSyntax = _getDelegateForFunctionPointerGenericExists
			? MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName(nameof(Marshal)), GenericName(nameof(Marshal.GetDelegateForFunctionPointer)).AddTypeArgumentListArguments(genericTypeParameter))
			: MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName(nameof(Marshal)), IdentifierName(nameof(Marshal.GetDelegateForFunctionPointer)));

		// Generate an argument list for the method call
		var argumentListSyntax = ArgumentList().AddArguments(Argument(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, ThisExpression(), valueFieldName)));
		if (!_getDelegateForFunctionPointerGenericExists)
			argumentListSyntax = argumentListSyntax.AddArguments(Argument(TypeOfExpression(genericTypeParameter)));

		// Generate "Marshal.GetDelegateForFunctionPointer<TDelegate>(this.Value)" or "(TDelegate)Marshal.GetDelegateForFunctionPointer(this.Value, typeof(TDelegate))"
		ExpressionSyntax bodyExpression = InvocationExpression(methodCallSyntax, argumentListSyntax);
		if (!_getDelegateForFunctionPointerGenericExists) bodyExpression = CastExpression(genericTypeParameter, bodyExpression);

		// Generate "internal TDelegate CreateDelegate<TDelegate>() where TDelegate : Delegate => Marshal.GetDelegateForFunctionPointer<TDelegate>(this.Value);"
		var methodDeclaration = MethodDeclaration(genericTypeParameter, Identifier("CreateDelegate"))
			.AddTypeParameterListParameters(TypeParameter(genericTypeParameter.Identifier))
			.AddConstraintClauses(TypeParameterConstraintClause(genericTypeParameter, SingletonSeparatedList<TypeParameterConstraintSyntax>(TypeConstraint(IdentifierName("Delegate")))))
			.WithExpressionBody(ArrowExpressionClause(bodyExpression))
			.AddModifiers(TokenWithSpace(Visibility))
			.WithSemicolonToken(SemicolonWithLineFeed);

		// Generate the complete struct declaration syntax tree
		StructDeclarationSyntax structDeclarationSyntax = StructDeclaration(typeName.Identifier)
			.WithModifiers(TokenList(TokenWithSpace(Visibility), TokenWithSpace(SyntaxKind.PartialKeyword)))
			.AddMembers(fieldDeclaration)
			.AddMembers(CreateCommonTypeDefMembers(typeName, IntPtrTypeSyntax, valueFieldName).ToArray())
			.AddMembers(methodDeclaration);

		return structDeclarationSyntax;
	}

	private void GetSignatureOfDelegate(TypeDefinition typeDefinition, out MethodDefinition invokeMethodDefinition, out MethodSignature<TypeHandleInfo> signature, out CustomAttributeHandleCollection? returnTypeAttributes)
	{
		// Get the "Invoke" method since delegates have an "Invoke" method with the parameters and the return type declared in the delegates, per ECMA-335
		invokeMethodDefinition = typeDefinition.GetMethods().Select(WinMDReader.GetMethodDefinition).Single(def => WinMDReader.StringComparer.Equals(def.Name, "Invoke"));

		// Get the parameters and the return type
		signature = invokeMethodDefinition.DecodeSignature(SignatureHandleProvider.Instance, null);
		returnTypeAttributes = GetReturnTypeCustomAttributes(invokeMethodDefinition);
	}
}
