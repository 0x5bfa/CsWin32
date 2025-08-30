namespace CsWin32Ex;

public partial class Generator
{
	internal FunctionPointerTypeSyntax FunctionPointer(QualifiedTypeDefinition delegateType)
	{
		if (delegateType.Generator != this)
		{
			FunctionPointerTypeSyntax? result = null;
			delegateType.Generator._volatileCode.GenerationTransaction(() => result = delegateType.Generator.FunctionPointer(delegateType.Definition));
			return result!;
		}
		else
		{
			return this.FunctionPointer(delegateType.Definition);
		}
	}

	internal FunctionPointerTypeSyntax FunctionPointer(TypeDefinition delegateType)
	{
		CustomAttribute ufpAtt = this.FindAttribute(delegateType.GetCustomAttributes(), SystemRuntimeInteropServices, nameof(UnmanagedFunctionPointerAttribute))!.Value;
		CustomAttributeValue<TypeSyntax> attArgs = ufpAtt.DecodeValue(CustomAttributeTypeProvider.Instance);
		var callingConvention = (CallingConvention)attArgs.FixedArguments[0].Value!;

		this.GetSignatureOfDelegate(delegateType, out MethodDefinition invokeMethodDef, out MethodSignature<TypeHandleInfo> signature, out CustomAttributeHandleCollection? returnTypeAttributes);
		if (this.FindAttribute(returnTypeAttributes, SystemRuntimeInteropServices, nameof(MarshalAsAttribute)).HasValue)
		{
			throw new NotSupportedException("Marshaling is not supported for function pointers.");
		}

		return this.FunctionPointer(invokeMethodDef, signature);
	}

	private FunctionPointerTypeSyntax FunctionPointer(MethodDefinition methodDefinition, MethodSignature<TypeHandleInfo> signature)
	{
		FunctionPointerCallingConventionSyntax callingConventionSyntax = FunctionPointerCallingConvention(
			Token(SyntaxKind.UnmanagedKeyword),
			FunctionPointerUnmanagedCallingConventionList(SingletonSeparatedList(ToUnmanagedCallingConventionSyntax(CallingConvention.StdCall))));

		FunctionPointerParameterListSyntax parametersList = FunctionPointerParameterList();

		foreach (ParameterHandle parameterHandle in methodDefinition.GetParameters())
		{
			Parameter parameter = this.WinMDReader.GetParameter(parameterHandle);
			if (parameter.SequenceNumber == 0)
			{
				continue;
			}

			TypeHandleInfo? parameterTypeInfo = signature.ParameterTypes[parameter.SequenceNumber - 1];
			parametersList = parametersList.AddParameters(this.TranslateDelegateToFunctionPointer(parameterTypeInfo, parameter.GetCustomAttributes()));
		}

		parametersList = parametersList.AddParameters(this.TranslateDelegateToFunctionPointer(signature.ReturnType, this.GetReturnTypeCustomAttributes(methodDefinition)));

		return FunctionPointerType(callingConventionSyntax, parametersList);
	}

	private FunctionPointerParameterSyntax TranslateDelegateToFunctionPointer(TypeHandleInfo parameterTypeInfo, CustomAttributeHandleCollection? customAttributeHandles)
	{
		if (this.IsDelegateReference(parameterTypeInfo, out QualifiedTypeDefinition delegateTypeDef))
		{
			return FunctionPointerParameter(delegateTypeDef.Generator.FunctionPointer(delegateTypeDef.Definition));
		}

		return FunctionPointerParameter(parameterTypeInfo.ToTypeSyntax(this._functionPointerTypeSettings, GeneratingElement.FunctionPointer, customAttributeHandles).GetUnmarshaledType());
	}
}
