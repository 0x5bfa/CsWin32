// Copyright (c) 0x5BFA.

using System.CodeDom.Compiler;

namespace CsWin32Ex;

public partial class Generator
{
	private StructDeclarationSyntax CreateStructDeclaration(TypeDefinitionHandle typeDefinitionHandle, Context context)
	{
		var typeDefinition = WinMDReader.GetTypeDefinition(typeDefinitionHandle);
		var isManagedType = IsManagedType(typeDefinitionHandle);
		var identifierNameSyntax = IdentifierName(GetMangledIdentifier(WinMDReader.GetString(typeDefinition.Name), context.AllowMarshaling, isManagedType));

		var explicitLayout = MetadataHelpers.HasTypeAttributesOf(typeDefinition, TypeAttributes.ExplicitLayout);
		if (explicitLayout)
			context = context with { AllowMarshaling = false };

		// Disable marshalling when the last field has the [FlexibleArrayAttribute]. The struct is only ever valid
		// when accessed via a pointer, since the struct acts as a header of an arbitrarily-sized array.
		if (MetadataHelpers.TryGetFlexibleArrayField(typeDefinition, WinMDReader, out var flexibleArrayFieldDefinitionHandle))
			context = context with { AllowMarshaling = false };

		MethodDeclarationSyntax? sizeOfMethod = null;
		TypeSyntaxSettings typeSettings = context.Filter(_fieldTypeSettings);
		bool hasUtf16CharField = false;
		var members = new List<MemberDeclarationSyntax>();
		SyntaxList<MemberDeclarationSyntax> additionalMembers = default;

		foreach (FieldDefinitionHandle fieldDefinitionHandle in typeDefinition.GetFields())
		{
			var fieldDefinition = WinMDReader.GetFieldDefinition(fieldDefinitionHandle);
			FieldDeclarationSyntax fieldDeclarationSyntax;

			if (MetadataHelpers.IsConstField(fieldDefinition))
			{
				fieldDeclarationSyntax = CreateConstantDeclaration(fieldDefinition);
				members.Add(fieldDeclarationSyntax);
				continue;
			}
			else if (MetadataHelpers.IsStaticField(fieldDefinition))
			{
				throw new NotSupportedException();
			}

			string fieldName = WinMDReader.GetString(fieldDefinition.Name);

			try
			{
				var fieldTypeInfo = fieldDefinition.DecodeSignature(SignatureHandleProvider.Instance, null);
				var fieldCustomAttributes = fieldDefinition.GetCustomAttributes();
				var fieldDeclarator = VariableDeclarator(SafeIdentifier(fieldName));

				// Check if the field has "fixed" keyword
				if (FeatureHelpers.HasFixedBufferAttribute(fieldCustomAttributes, WinMDReader, out _))
				{
					if (FeatureHelpers.TryExtractFixedBufferAttribute(fieldCustomAttributes, WinMDReader, out var fieldTypeSyntax, out var fieldArraySizeSyntax) &&
						LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(fieldArraySizeSyntax)) is ExpressionSyntax arraySizeExpressionSyntax)
					{
						// ================================================================================
						//   internal unsafe fixed char Name[32];
						// ================================================================================
						// Becomes
						// ================================================================================
						//   [StructLayout(LayoutKind.Sequential, Size = 64), CompilerGenerated, UnsafeValueType]
						//   public struct <StandardName>e__FixedBuffer { public char FixedElementField; }
						//   [FixedBuffer(typeof(char), 32)]
						//   internal <StandardName>e__FixedBuffer StandardName;
						// ================================================================================
						// So, we need to revert it back to the original form.
						fieldDeclarationSyntax = FieldDeclaration(
							VariableDeclaration(fieldTypeSyntax))
							.AddDeclarationVariables(
								fieldDeclarator
									.WithArgumentList(BracketedArgumentList(SingletonSeparatedList(Argument(arraySizeExpressionSyntax)))))
							.AddModifiers(TokenWithSpace(Visibility), TokenWithSpace(SyntaxKind.UnsafeKeyword), Token(SyntaxKind.FixedKeyword));
					}
					else
					{
						throw new InvalidOperationException("The fixed keyword is not correctly used.");
					}
				}
				// Check if the field has "[FlexibleArrayAttribute]" and generate a helper struct for a variable-length inline array, if any
				else if (fieldDefinitionHandle == flexibleArrayFieldDefinitionHandle)
				{
					if (fieldTypeInfo is ArrayTypeHandleInfo arrayTypeHandleInfo &&
						arrayTypeHandleInfo.ElementType.ToTypeSyntax(typeSettings, GeneratingElement.StructMember, fieldCustomAttributes).Type is { } fieldTypeSyntax)
					{
						// If the field is a pointer or a function pointer, declare as-is
						// since the helper struct takes a generic type argument but these types aren't supported
						// For more information, see https://github.com/dotnet/runtime/issues/13627
						if (fieldTypeSyntax is PointerTypeSyntax or FunctionPointerTypeSyntax)
						{
							var variableLengthInlineHelperStructDeclarationSyntax = DeclareVariableLengthInlineArrayHelper(context, fieldTypeSyntax);
							additionalMembers = additionalMembers.Add(variableLengthInlineHelperStructDeclarationSyntax);

							fieldDeclarationSyntax = FieldDeclaration(
								VariableDeclaration(IdentifierName(variableLengthInlineHelperStructDeclarationSyntax.Identifier.ValueText)))
									.AddDeclarationVariables(fieldDeclarator)
									.AddModifiers(TokenWithSpace(Visibility));
						}
						// If the type of the field is "System.Char", generate a helper struct since it is not blittable
						else if (fieldTypeSyntax is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.CharKeyword })
						{
							// "internal VariableLengthInlineArray<char, ushort> fieldName;"
							RequestVariableLengthInlineArrayHelper2(context);
							fieldDeclarationSyntax = FieldDeclaration(
								VariableDeclaration(
									GenericName($"global::Windows.Win32.VariableLengthInlineArray")
										.WithTypeArgumentList(TypeArgumentList().AddArguments(fieldTypeSyntax, PredefinedType(Token(SyntaxKind.UShortKeyword))))))
									.AddDeclarationVariables(fieldDeclarator)
									.AddModifiers(TokenWithSpace(Visibility));
						}
						else
						{
							// "internal VariableLengthInlineArrayHelper fieldName;"
							RequestVariableLengthInlineArrayHelper1(context);
							fieldDeclarationSyntax = FieldDeclaration(
								VariableDeclaration(
									GenericName($"global::Windows.Win32.VariableLengthInlineArray")
									.WithTypeArgumentList(TypeArgumentList().AddArguments(fieldTypeSyntax))))
								.AddDeclarationVariables(fieldDeclarator)
								.AddModifiers(TokenWithSpace(Visibility));
						}

						// "internal static unsafe int SizeOf(int count) { }"
						sizeOfMethod = CreateFlexibleArraySizeOfMethodDeclaration(identifierNameSyntax, fieldTypeSyntax, typeSettings);
					}
					else
					{
						throw new InvalidOperationException("The FlexibleArrayAttribute is not correctly interpreted.");
					}

				}
				// Check for other cases
				else
				{
					hasUtf16CharField |= fieldTypeInfo is PrimitiveTypeHandleInfo { PrimitiveTypeCode: PrimitiveTypeCode.Char };
					var thisFieldTypeSettings = typeSettings;

					// Do not qualify names of a type nested inside *this* struct, since this struct may or may not have a mangled name.
					if (thisFieldTypeSettings.QualifyNames &&
						fieldTypeInfo is HandleTypeHandleInfo fieldHandleTypeInfo &&
						IsNestedType(fieldHandleTypeInfo.Handle) &&
						fieldHandleTypeInfo.Handle.Kind is HandleKind.TypeReference &&
						TryGetTypeDefHandle((TypeReferenceHandle)fieldHandleTypeInfo.Handle, out QualifiedTypeDefinitionHandle fieldTypeDefinitionHandle) &&
						fieldTypeDefinitionHandle.Generator == this)
					{
						foreach (TypeDefinitionHandle nestedTypeDefinitionHandle in typeDefinition.GetNestedTypes())
						{
							if (fieldTypeDefinitionHandle.DefinitionHandle == nestedTypeDefinitionHandle)
							{
								thisFieldTypeSettings = thisFieldTypeSettings with { QualifyNames = false };
								break;
							}
						}
					}

					var fieldTypeSyntax = fieldTypeInfo.ToTypeSyntax(thisFieldTypeSettings, GeneratingElement.StructMember, fieldCustomAttributes);
					var fieldInfo = ReinterpretFieldType(fieldDefinition, fieldTypeSyntax.Type, fieldCustomAttributes, context);
					additionalMembers = additionalMembers.AddRange(fieldInfo.AdditionalMembers);

					PropertyDeclarationSyntax? property = null;

					if (FeatureHelpers.TryExtractAssociatedEnumAttribute(fieldCustomAttributes, WinMDReader, out var enumIdentifierNameSyntax))
					{
						// Keep the field with its original type, but then add a property that returns the enum type
						// ================================================================================
						//   private OriginalType _FieldName;
						// ================================================================================
						fieldDeclarator = VariableDeclarator(SafeIdentifier($"_{fieldName}"));
						fieldDeclarationSyntax = FieldDeclaration(VariableDeclaration(fieldInfo.FieldType).AddVariables(fieldDeclarator))
							.AddModifiers(TokenWithSpace(SyntaxKind.PrivateKeyword));

						// Generate a property for the backing field
						// ================================================================================
						//   internal EnumType FieldName
						//   {
						//       get => (EnumType)_FieldName;
						//       set => _FieldName = (OriginalType)value;
						//   }
						// ================================================================================
						RequestInteropType(GetNamespaceForPossiblyNestedType(typeDefinition), enumIdentifierNameSyntax.Identifier.ValueText, context);
						ExpressionSyntax fieldAccess = MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, ThisExpression(), IdentifierName(fieldDeclarator.Identifier));
						property = PropertyDeclaration(enumIdentifierNameSyntax.WithTrailingTrivia(Space), Identifier(fieldName).WithTrailingTrivia(LineFeed))
							.AddModifiers(TokenWithSpace(Visibility))
							.WithAccessorList(AccessorList().AddAccessors(
								AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithExpressionBody(ArrowExpressionClause(CastExpression(enumIdentifierNameSyntax, fieldAccess))).WithSemicolonToken(Semicolon),
								AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithExpressionBody(ArrowExpressionClause(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, fieldAccess, CastExpression(fieldInfo.FieldType, IdentifierName("value"))))).WithSemicolonToken(Semicolon)));

						additionalMembers = additionalMembers.Add(property);
					}
					else
					{
						// Just generate a normal field
						fieldDeclarationSyntax = FieldDeclaration(VariableDeclaration(fieldInfo.FieldType).AddVariables(fieldDeclarator))
							.AddModifiers(TokenWithSpace(Visibility));
					}

					// Add [MarshalAsAttribute] if needed
					if (fieldInfo.MarshalAsAttribute is not null)
						fieldDeclarationSyntax = fieldDeclarationSyntax.AddAttributeLists(AttributeList().AddAttributes(fieldInfo.MarshalAsAttribute));

					// Add [ObsoleteAttribute] if needed
					if (FeatureHelpers.HasObsoleteAttribute(fieldDefinition.GetCustomAttributes(), WinMDReader, out _))
					{
						fieldDeclarationSyntax = fieldDeclarationSyntax.AddAttributeLists(AttributeList().AddAttributes(ObsoleteAttributeSyntax));
						property = property?.AddAttributeLists(AttributeList().AddAttributes(ObsoleteAttributeSyntax));
					}

					// Add "unsafe" modifier if needed
					if (RequiresUnsafe(fieldInfo.FieldType))
						fieldDeclarationSyntax = fieldDeclarationSyntax.AddModifiers(TokenWithSpace(SyntaxKind.UnsafeKeyword));

					// Add "new" modifier if needed
					if (ObjectMembers.Contains(fieldName))
						fieldDeclarationSyntax = fieldDeclarationSyntax.AddModifiers(TokenWithSpace(SyntaxKind.NewKeyword));
				}

				// Add a [FieldOffsetAttribute], If the field offset is explicitly set
				int offset = fieldDefinition.GetOffset();
				if (offset >= 0)
					fieldDeclarationSyntax = fieldDeclarationSyntax.AddAttributeLists(AttributeList().AddAttributes(FieldOffset(offset)));

				members.Add(fieldDeclarationSyntax);

				if (FeatureHelpers.TryExtractNativeBitfieldAttributes(fieldCustomAttributes, WinMDReader, out var nativeBitfieldAttributes))
				{
					foreach (var nativeBitfieldAttribute in nativeBitfieldAttributes)
					{
						if (fieldTypeInfo is PrimitiveTypeHandleInfo primitiveTypeHandleInfo &&
							nativeBitfieldAttribute.DecodeValue(CustomAttributeTypeProvider.Instance) is { } decodedAttribute &&
							decodedAttribute.FixedArguments[0].Value is string propertyName &&
							decodedAttribute.FixedArguments[1].Value is long propertyOffset &&
							decodedAttribute.FixedArguments[2].Value is long propertyLength)
						{
							var propertyOffsetAsByte = (byte)propertyOffset;
							var propertyLengthAsByte = (byte)propertyLength;

							// D3DKMDT_DISPLAYMODE_FLAGS has an "Anonymous" 0-length bitfield,
							// but that's totally useless and breaks our math later on, so skip it.
							if (propertyLengthAsByte is 0)
								continue;

							var (lengthInBits, signed) = MetadataHelpers.GetLengthInBits(primitiveTypeHandleInfo.PrimitiveTypeCode);

							long minValue = signed ? -(1L << (propertyLengthAsByte - 1)) : 0;
							long maxValue = (1L << (propertyLengthAsByte - (signed ? 1 : 0))) - 1;
							int? leftPad = lengthInBits.HasValue ? lengthInBits - (propertyOffsetAsByte + propertyLengthAsByte) : null;
							int rightPad = propertyOffsetAsByte;
							(TypeSyntax propertyType, int propertyBitLength) = propertyLengthAsByte switch
							{
								1 => (PredefinedType(Token(SyntaxKind.BoolKeyword)), 1),
								<= 8 => (PredefinedType(Token(signed ? SyntaxKind.SByteKeyword : SyntaxKind.ByteKeyword)), 8),
								<= 16 => (PredefinedType(Token(signed ? SyntaxKind.ShortKeyword : SyntaxKind.UShortKeyword)), 16),
								<= 32 => (PredefinedType(Token(signed ? SyntaxKind.IntKeyword : SyntaxKind.UIntKeyword)), 32),
								<= 64 => (PredefinedType(Token(signed ? SyntaxKind.LongKeyword : SyntaxKind.ULongKeyword)), 64),
								_ => throw new NotSupportedException(),
							};

							// [MethodImpl(MethodImplOptions.AggressiveInlining)]
							// readonly get => (byte)((this._bitfield >> 0) & 0x000000000000001F);

							var getter = AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
								.AddModifiers(TokenWithSpace(SyntaxKind.ReadOnlyKeyword))
								.AddAttributeLists(AttributeList().AddAttributes(MethodImpl(MethodImplOptions.AggressiveInlining)));
							var setter = AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
								.AddAttributeLists(AttributeList().AddAttributes(MethodImpl(MethodImplOptions.AggressiveInlining)));

							ulong maskNoOffset = (1UL << propertyLengthAsByte) - 1;
							ulong mask = maskNoOffset << propertyOffsetAsByte;
							int fieldLengthInHexChars = MetadataHelpers.GetLengthInBytes(primitiveTypeHandleInfo.PrimitiveTypeCode) * 2;
							LiteralExpressionSyntax maskExpr = LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(ToHex(mask, fieldLengthInHexChars), mask));

							ExpressionSyntax fieldAccess = MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, ThisExpression(), IdentifierName(fieldName));
							TypeSyntax fieldType = fieldDeclarationSyntax.Declaration.Type.WithoutTrailingTrivia();

							//// unchecked((int)~mask)
							ExpressionSyntax notMask = UncheckedExpression(CastExpression(fieldType, PrefixUnaryExpression(SyntaxKind.BitwiseNotExpression, maskExpr)));
							//// (field & unchecked((int)~mask))
							ExpressionSyntax fieldAndNotMask = ParenthesizedExpression(BinaryExpression(SyntaxKind.BitwiseAndExpression, fieldAccess, notMask));

							if (propertyLengthAsByte > 1)
							{
								LiteralExpressionSyntax maskNoOffsetExpr = LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(ToHex(maskNoOffset, fieldLengthInHexChars), maskNoOffset));
								ExpressionSyntax notMaskNoOffset = UncheckedExpression(CastExpression(propertyType, PrefixUnaryExpression(SyntaxKind.BitwiseNotExpression, maskNoOffsetExpr)));
								LiteralExpressionSyntax propOffsetExpr = LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(propertyOffsetAsByte));

								// signed:
								// get => (byte)((field << leftPad) >> (leftPad + rightPad)));
								// unsigned:
								// get => (byte)((field >> rightPad) & maskNoOffset);
								ExpressionSyntax getterExpression =
									CastExpression(propertyType, ParenthesizedExpression(
										signed ?
											BinaryExpression(
												SyntaxKind.RightShiftExpression,
												ParenthesizedExpression(BinaryExpression(
													SyntaxKind.LeftShiftExpression,
													fieldAccess,
													LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(leftPad!.Value)))),
												LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(leftPad.Value + rightPad)))
											: BinaryExpression(
												SyntaxKind.BitwiseAndExpression,
												ParenthesizedExpression(BinaryExpression(SyntaxKind.RightShiftExpression, fieldAccess, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(rightPad)))),
												maskNoOffsetExpr)));
								getter = getter
									.WithExpressionBody(ArrowExpressionClause(getterExpression))
									.WithSemicolonToken(SemicolonWithLineFeed);

								IdentifierNameSyntax valueName = IdentifierName("value");

								List<StatementSyntax> setterStatements = new();
								if (propertyBitLength > propertyLengthAsByte)
								{
									// The allowed range is smaller than the property type, so we need to check that the value fits.
									// signed:
									//  global::System.Debug.Assert(value is >= minValue and <= maxValue);
									// unsigned:
									//  global::System.Debug.Assert(value is <= maxValue);
									RelationalPatternSyntax max = RelationalPattern(TokenWithSpace(SyntaxKind.LessThanEqualsToken), CastExpression(propertyType, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(maxValue))));
									RelationalPatternSyntax? min = signed ? RelationalPattern(TokenWithSpace(SyntaxKind.GreaterThanEqualsToken), CastExpression(propertyType, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(minValue)))) : null;
									setterStatements.Add(ExpressionStatement(InvocationExpression(
										ParseName("global::System.Diagnostics.Debug.Assert"),
										ArgumentList().AddArguments(Argument(
											IsPatternExpression(
												valueName,
												min is null ? max : BinaryPattern(SyntaxKind.AndPattern, min, max)))))));
								}

								// field = (int)((field & unchecked((int)~mask)) | ((int)(value & mask) << propOffset)));
								ExpressionSyntax valueAndMaskNoOffset = ParenthesizedExpression(BinaryExpression(SyntaxKind.BitwiseAndExpression, valueName, maskNoOffsetExpr));
								setterStatements.Add(ExpressionStatement(AssignmentExpression(
									SyntaxKind.SimpleAssignmentExpression,
									fieldAccess,
									CastExpression(fieldType, ParenthesizedExpression(
										BinaryExpression(
											SyntaxKind.BitwiseOrExpression,
											//// (field & unchecked((int)~mask))
											fieldAndNotMask,
											//// ((int)(value & mask) << propOffset)
											ParenthesizedExpression(BinaryExpression(SyntaxKind.LeftShiftExpression, CastExpression(fieldType, valueAndMaskNoOffset), propOffsetExpr))))))));
								setter = setter.WithBody(Block().AddStatements(setterStatements.ToArray()));
							}
							else
							{
								// get => (field & getterMask) != 0;
								getter = getter
									.WithExpressionBody(ArrowExpressionClause(BinaryExpression(
										SyntaxKind.NotEqualsExpression,
										ParenthesizedExpression(BinaryExpression(SyntaxKind.BitwiseAndExpression, fieldAccess, maskExpr)),
										LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0)))))
									.WithSemicolonToken(SemicolonWithLineFeed);

								// set => field = (byte)(value ? field | getterMask : field & unchecked((int)~getterMask));
								setter = setter
									.WithExpressionBody(ArrowExpressionClause(
										AssignmentExpression(
											SyntaxKind.SimpleAssignmentExpression,
											fieldAccess,
											CastExpression(
												fieldType,
												ParenthesizedExpression(
													ConditionalExpression(
														IdentifierName("value"),
														BinaryExpression(SyntaxKind.BitwiseOrExpression, fieldAccess, maskExpr),
														fieldAndNotMask))))))
									.WithSemicolonToken(SemicolonWithLineFeed);
							}

							string bitDescription = propertyLengthAsByte == 1 ? $"bit {propertyOffsetAsByte}" : $"bits {propertyOffsetAsByte}-{propertyOffsetAsByte + propertyLengthAsByte - 1}";
							string allowedRange = propertyLengthAsByte == 1 ? string.Empty : $"Allowed values are [{minValue}..{maxValue}].";

							PropertyDeclarationSyntax bitfieldProperty = PropertyDeclaration(propertyType.WithTrailingTrivia(Space), Identifier(propertyName).WithTrailingTrivia(LineFeed))
								.AddModifiers(TokenWithSpace(Visibility))
								.WithAccessorList(AccessorList().AddAccessors(getter, setter))
								.WithLeadingTrivia(ParseLeadingTrivia($"/// <summary>Gets or sets {bitDescription} in the <see cref=\"{fieldName}\" /> field.{allowedRange}</summary>\n"));

							StringBuilder stringBuilder = new();
							stringBuilder.AppendLine($"/// <summary>Gets or sets bits {bitDescription} in the <see cref=\"{fieldName}\" /> field.{allowedRange}.</summary>");
							stringBuilder.AppendLine($"{SyntaxFacts.GetText(Visibility)} {propertyType} {propertyName}");
							stringBuilder.AppendLine($"{{");
							stringBuilder.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
							stringBuilder.AppendLine($"    readonly get => (byte)((this._bitfield >> 0) & 0x000000000000001F);");
							stringBuilder.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
							stringBuilder.AppendLine($"    set");
							stringBuilder.AppendLine($"    {{");
							stringBuilder.AppendLine($"        global::System.Diagnostics.Debug.Assert(value is <= (byte)31L);");
							stringBuilder.AppendLine($"        this._bitfield = (nuint)((this._bitfield & unchecked((nuint)~0x000000000000001F)) | ((nuint)(value & 0x000000000000001F) << 0));");
							stringBuilder.AppendLine($"    }}");
							stringBuilder.AppendLine($"}}");

							var bitfieldProperty2 = SyntaxFactory.ParseMemberDeclaration(stringBuilder.ToString()) as PropertyDeclarationSyntax;

							members.Add(bitfieldProperty);
						}
					}
				}
			}
			catch (Exception ex)
			{
				throw new GenerationFailedException("Failed while generating field: " + fieldName, ex);
			}
		}

		// Add a SizeOf method, if there is a field with [FlexibleArrayAttribute]
		if (sizeOfMethod is not null)
			members.Add(sizeOfMethod);

		// Add the additional members, taking care to not introduce redundant declarations.
		members.AddRange(additionalMembers.Where(c =>
			c is not StructDeclarationSyntax cs ||
			!members.OfType<StructDeclarationSyntax>().Any(m => m.Identifier.ValueText == cs.Identifier.ValueText)));

		// Add the additional members from the templates
		switch (identifierNameSyntax.Identifier.ValueText)
		{
			case "RECT":
			case "SIZE":
			case "SYSTEMTIME":
			case "DECIMAL":
				members.AddRange(ExtractMembersFromTemplate(identifierNameSyntax.Identifier.ValueText));
				break;
			default:
				break;
		}

		// Now create the struct declaration syntax with the members
		StructDeclarationSyntax structDeclarationSyntax =
			StructDeclaration(identifierNameSyntax.Identifier)
				.AddMembers(members.ToArray())
				.WithModifiers(TokenList(TokenWithSpace(Visibility), TokenWithSpace(SyntaxKind.PartialKeyword)));

		// Add [StructLayoutAttribute] with the appropriate char set
		TypeLayout layout = typeDefinition.GetLayout();
		CharSet charSet = hasUtf16CharField ? CharSet.Unicode : CharSet.Ansi;
		if (!layout.IsDefault || explicitLayout || charSet is not CharSet.Ansi)
			structDeclarationSyntax = structDeclarationSyntax.AddAttributeLists(AttributeList().AddAttributes(StructLayout(typeDefinition.Attributes, layout, charSet)));

		// Add [GuidAttribute] if needed
		if (FindGuidFromAttribute(typeDefinition) is Guid guid)
			structDeclarationSyntax = structDeclarationSyntax.AddAttributeLists(AttributeList().AddAttributes(GUID(guid)));

		// Add XML comments
		structDeclarationSyntax = AppendXmlCommentTo(identifierNameSyntax.Identifier.ValueText, structDeclarationSyntax);

		return structDeclarationSyntax;
	}

	private StructDeclarationSyntax DeclareVariableLengthInlineArrayHelper(Context context, TypeSyntax fieldType)
	{
		IdentifierNameSyntax firstElementFieldName = IdentifierName("e0");
		List<MemberDeclarationSyntax> members = new();

		// internal unsafe T e0;
		members.Add(FieldDeclaration(VariableDeclaration(fieldType).AddVariables(VariableDeclarator(firstElementFieldName.Identifier)))
			.AddModifiers(TokenWithSpace(Visibility), TokenWithSpace(SyntaxKind.UnsafeKeyword)));

		if (_canUseUnsafeAdd)
		{
			////[MethodImpl(MethodImplOptions.AggressiveInlining)]
			////get { fixed (int** p = &e0) return *(p + index); }
			IdentifierNameSyntax pLocal = IdentifierName("p");
			AccessorDeclarationSyntax getter = AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
				.WithBody(Block().AddStatements(
					FixedStatement(
						VariableDeclaration(PointerType(fieldType)).AddVariables(
							VariableDeclarator(pLocal.Identifier).WithInitializer(EqualsValueClause(PrefixUnaryExpression(SyntaxKind.AddressOfExpression, firstElementFieldName)))),
						ReturnStatement(PrefixUnaryExpression(SyntaxKind.PointerIndirectionExpression, ParenthesizedExpression(BinaryExpression(SyntaxKind.AddExpression, pLocal, IdentifierName("index"))))))))
				.AddAttributeLists(AttributeList().AddAttributes(MethodImpl(MethodImplOptions.AggressiveInlining)));

			////[MethodImpl(MethodImplOptions.AggressiveInlining)]
			////set { fixed (int** p = &e0) *(p + index) = value; }
			AccessorDeclarationSyntax setter = AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
				.WithBody(Block().AddStatements(
					FixedStatement(
						VariableDeclaration(PointerType(fieldType)).AddVariables(
							VariableDeclarator(pLocal.Identifier).WithInitializer(EqualsValueClause(PrefixUnaryExpression(SyntaxKind.AddressOfExpression, firstElementFieldName)))),
						ExpressionStatement(AssignmentExpression(
							SyntaxKind.SimpleAssignmentExpression,
							PrefixUnaryExpression(SyntaxKind.PointerIndirectionExpression, ParenthesizedExpression(BinaryExpression(SyntaxKind.AddExpression, pLocal, IdentifierName("index")))),
							IdentifierName("value"))))))
				.AddAttributeLists(AttributeList().AddAttributes(MethodImpl(MethodImplOptions.AggressiveInlining)));

			////internal unsafe T this[int index]
			members.Add(IndexerDeclaration(fieldType.WithTrailingTrivia(Space))
				.AddModifiers(TokenWithSpace(Visibility), TokenWithSpace(SyntaxKind.UnsafeKeyword))
				.AddParameterListParameters(Parameter(Identifier("index")).WithType(PredefinedType(TokenWithSpace(SyntaxKind.IntKeyword))))
				.AddAccessorListAccessors(getter, setter));
		}

		// internal partial struct VariableLengthInlineArrayHelper
		return StructDeclaration(Identifier("VariableLengthInlineArrayHelper"))
			.AddModifiers(TokenWithSpace(Visibility), TokenWithSpace(SyntaxKind.PartialKeyword))
			.AddMembers(members.ToArray());
	}

	private MethodDeclarationSyntax CreateFlexibleArraySizeOfMethodDeclaration(TypeSyntax structType, TypeSyntax elementType, TypeSyntaxSettings typeSettings)
	{
		PredefinedTypeSyntax intType = PredefinedType(TokenWithSpace(SyntaxKind.IntKeyword));
		IdentifierNameSyntax countName = IdentifierName("count");
		IdentifierNameSyntax localName = IdentifierName("v");
		List<StatementSyntax> statements = new();

		// int v = sizeof(OUTER_STRUCT);
		statements.Add(LocalDeclarationStatement(VariableDeclaration(intType).AddVariables(
			VariableDeclarator(localName.Identifier).WithInitializer(EqualsValueClause(SizeOfExpression(structType))))));

		// if (count > 1)
		//   v += checked((count - 1) * sizeof(ELEMENT_TYPE));
		// else if (count < 0)
		//   throw new ArgumentOutOfRangeException(nameof(count));
		statements.Add(IfStatement(
			BinaryExpression(SyntaxKind.GreaterThanExpression, countName, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1))),
			ExpressionStatement(AssignmentExpression(
				SyntaxKind.AddAssignmentExpression,
				localName,
				CheckedExpression(BinaryExpression(
					SyntaxKind.MultiplyExpression,
					ParenthesizedExpression(BinaryExpression(SyntaxKind.SubtractExpression, countName, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1)))),
					SizeOfExpression(elementType))))),
			ElseClause(IfStatement(
				BinaryExpression(SyntaxKind.LessThanExpression, countName, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))),
				ThrowStatement(ObjectCreationExpression(IdentifierName(nameof(ArgumentOutOfRangeException))))).WithCloseParenToken(TokenWithLineFeed(SyntaxKind.CloseParenToken)))).WithCloseParenToken(TokenWithLineFeed(SyntaxKind.CloseParenToken)));

		// return v;
		statements.Add(ReturnStatement(localName));

		// internal static unsafe int SizeOf(int count)
		MethodDeclarationSyntax sizeOfMethod = MethodDeclaration(intType, Identifier("SizeOf"))
			.AddParameterListParameters(Parameter(countName.Identifier).WithType(intType))
			.WithBody(Block().AddStatements(statements.ToArray()))
			.AddModifiers(TokenWithSpace(Visibility), TokenWithSpace(SyntaxKind.StaticKeyword), TokenWithSpace(SyntaxKind.UnsafeKeyword))
			.WithLeadingTrivia(ParseLeadingTrivia("/// <summary>Computes the amount of memory that must be allocated to store this struct, including the specified number of elements in the variable length inline array at the end.</summary>\n"));

		return sizeOfMethod;
	}

	private (TypeSyntax FieldType, SyntaxList<MemberDeclarationSyntax> AdditionalMembers, AttributeSyntax? MarshalAsAttribute) ReinterpretFieldType(FieldDefinition fieldDef, TypeSyntax originalType, CustomAttributeHandleCollection customAttributes, Context context)
	{
		TypeSyntaxSettings typeSettings = context.Filter(_fieldTypeSettings);
		TypeHandleInfo fieldTypeHandleInfo = fieldDef.DecodeSignature(SignatureHandleProvider.Instance, null);
		AttributeSyntax? marshalAs = null;

		// If the field is a fixed length array, we have to work some code gen magic since C# does not allow those.
		if (originalType is ArrayTypeSyntax arrayType && arrayType.RankSpecifiers.Count > 0 && arrayType.RankSpecifiers[0].Sizes.Count == 1)
		{
			return DeclareFixedLengthArrayStruct(fieldDef, customAttributes, fieldTypeHandleInfo, arrayType, context);
		}

		// If the field is a delegate type, we have to replace that with a native function pointer to avoid the struct becoming a 'managed type'.
		if ((!context.AllowMarshaling) && IsDelegateReference(fieldTypeHandleInfo, out QualifiedTypeDefinition typeDef) && !typeDef.Generator.IsUntypedDelegate(typeDef.Definition))
		{
			return (FunctionPointer(typeDef), default(SyntaxList<MemberDeclarationSyntax>), marshalAs);
		}

		// If the field is a pointer to a COM interface (and we're using bona fide interfaces),
		// then we must type it as an array.
		if (context.AllowMarshaling && fieldTypeHandleInfo is PointerTypeHandleInfo ptr3 && IsInterface(ptr3.ElementType))
		{
			return (ArrayType(ptr3.ElementType.ToTypeSyntax(typeSettings, GeneratingElement.Field, null).Type).AddRankSpecifiers(ArrayRankSpecifier()), default(SyntaxList<MemberDeclarationSyntax>), marshalAs);
		}

		return (originalType, default(SyntaxList<MemberDeclarationSyntax>), marshalAs);
	}

	private void RequestVariableLengthInlineArrayHelper1(Context context)
	{
		if (IsWin32Sdk)
		{
			if (!IsTypeAlreadyFullyDeclared($"{Namespace}.{_variableLengthInlineArrayStruct1.Identifier.ValueText}`1"))
			{
				DeclareUnscopedRefAttributeIfNecessary();
				_volatileCode.GenerateSpecialType("VariableLengthInlineArray1", () => _volatileCode.AddSpecialType("VariableLengthInlineArray1", _variableLengthInlineArrayStruct1));
			}
		}
		else if (Manager is not null && Manager.TryGetGenerator("Windows.Win32", out Generator? generator))
		{
			generator._volatileCode.GenerationTransaction(delegate
			{
				generator.RequestVariableLengthInlineArrayHelper1(context);
			});
		}
	}

	private void RequestVariableLengthInlineArrayHelper2(Context context)
	{
		if (IsWin32Sdk)
		{
			if (!IsTypeAlreadyFullyDeclared($"{Namespace}.{variableLengthInlineArrayStruct2.Identifier.ValueText}`2"))
			{
				DeclareUnscopedRefAttributeIfNecessary();
				_volatileCode.GenerateSpecialType("VariableLengthInlineArray2", () => _volatileCode.AddSpecialType("VariableLengthInlineArray2", variableLengthInlineArrayStruct2));
			}
		}
		else if (Manager is not null && Manager.TryGetGenerator("Windows.Win32", out Generator? generator))
		{
			generator._volatileCode.GenerationTransaction(delegate
			{
				generator.RequestVariableLengthInlineArrayHelper2(context);
			});
		}
	}
}
