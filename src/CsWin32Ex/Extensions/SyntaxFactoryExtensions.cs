// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

internal static class SyntaxFactoryHelpers
{
	internal static IdentifierNameSyntax CreateIdentifierNameSyntaxFromName(string identifierName)
	{
		return SyntaxFactory.IdentifierName(SyntaxFactory.Identifier(SyntaxFactory.TriviaList(), identifierName, SyntaxFactory.TriviaList()));
	}

	internal static AccessorDeclarationSyntax CreateGetterAccessorDeclarationSyntaxWith()
	{
		return SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration);
	}
}
