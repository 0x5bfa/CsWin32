// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

public partial class Generator
{
	internal ApiDocumentationProvider? ApiDocumentationProvider { get; }

	private T AppendXmlCommentTo<T>(string typeName, T memberDeclaration) where T : MemberDeclarationSyntax
	{
		// If there's no documentation on this API, return the member declaration as-is
		if (ApiDocumentationProvider is null || !ApiDocumentationProvider.TryGetApiDocs(typeName, out var docs))
			return memberDeclaration;

		var xmlCommentBuilder = new XmlCommentBuilder();

		// Add the <summary> element
		if (docs.Description is not null)
		{
			xmlCommentBuilder
				.StartSummaryElement()
				.AppendParagraphs(docs.Description)
				.EndSummaryElement();
		}

		// Add the <param> elements
		if (docs.Parameters is not null && memberDeclaration is BaseMethodDeclarationSyntax methodDeclaration)
		{
			foreach (KeyValuePair<string, string> entry in docs.Parameters)
			{
				// Skip documentation for parameters that do not actually exist on the method.
				if (!methodDeclaration.ParameterList.Parameters.Any(p => string.Equals(p.Identifier.ValueText, entry.Key, StringComparison.Ordinal)))
					continue;

				xmlCommentBuilder
					.StartParamElement(entry.Key)
					.AppendParagraphs(entry.Value)
					.AppendToLearnMoreVisitLinkText(docs.HelpLink, "parameters")
					.EndParamElement();
			}
		}

		// Add the <returns> element
		if (docs.ReturnValue is not null)
		{
			xmlCommentBuilder
				.StartReturnsElement()
				.AppendParagraphs(docs.ReturnValue)
				.AppendToLearnMoreVisitLinkText(docs.HelpLink, "return-value")
				.EndReturnsElement();
		}

		// Add the <remarks> element
		if (docs.Remarks is not null || docs.HelpLink is not null)
		{
			xmlCommentBuilder.StartRemarksElement();

			if (docs.Remarks is not null)
				xmlCommentBuilder.AppendParagraphs(docs.Remarks);
			if (docs.HelpLink is not null)
				xmlCommentBuilder.AppendToLearnMoreVisitLinkText(docs.HelpLink, "remarks");

			xmlCommentBuilder.EndRemarksElement();
		}

		// If this type is a struct or an enum, add XML comments to its members too
		if (docs.Fields is not null)
		{
			var xmlCommentBuilderForField = new XmlCommentBuilder();

			if (memberDeclaration is StructDeclarationSyntax structDeclaration)
			{
				memberDeclaration = memberDeclaration.ReplaceNodes(
					structDeclaration.Members.OfType<FieldDeclarationSyntax>(),
					(_, field) =>
					{
						VariableDeclaratorSyntax? variable = field.Declaration.Variables.Single();
						if (docs.Fields.TryGetValue(variable.Identifier.ValueText, out string? fieldSummary))
						{
							xmlCommentBuilderForField.StartSummaryElement()
								.AppendParagraphs(fieldSummary)
								.AppendToLearnMoreVisitLinkText(docs.HelpLink, "members")
								.EndSummaryElement();

							if (field.Declaration.Type.HasAnnotations(OriginalDelegateAnnotation) &&
								field.Declaration.Type.GetAnnotations(OriginalDelegateAnnotation).Single().Data?.ToString() is { } originalDelegateDefinitionDocsUrl)
								xmlCommentBuilderForField.AppendLine($"/// <remarks>See the <see cref=\"{originalDelegateDefinitionDocsUrl}\" /> delegate for more about this struct.</remarks>");

							field = field.WithLeadingTrivia(ParseLeadingTrivia(xmlCommentBuilderForField.ToString()));
							xmlCommentBuilderForField.Clear();
						}

						return field;
					});
			}
			else if (memberDeclaration is EnumDeclarationSyntax enumDeclaration)
			{
				memberDeclaration = memberDeclaration.ReplaceNodes(
					enumDeclaration.Members,
					(_, field) =>
					{
						if (docs.Fields.TryGetValue(field.Identifier.ValueText, out string? fieldSummary))
						{
							xmlCommentBuilderForField.StartSummaryElement()
								.AppendParagraphs(fieldSummary)
								.AppendToLearnMoreVisitLinkText(docs.HelpLink, "members")
								.EndSummaryElement();

							field = field.WithLeadingTrivia(ParseLeadingTrivia(xmlCommentBuilderForField.ToString()));
							xmlCommentBuilderForField.Clear();
						}

						return field;
					});
			}
		}

		// Parse the constructed XML comment into a leading trivia and append it to the member declaration
		memberDeclaration = memberDeclaration.WithLeadingTrivia(ParseLeadingTrivia(xmlCommentBuilder.ToString()));
		xmlCommentBuilder.Clear();

		return memberDeclaration;
	}
}
