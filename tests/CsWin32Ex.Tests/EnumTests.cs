// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class EnumTests : GeneratorTestBase
{
	public EnumTests(ITestOutputHelper logger) : base(logger)
	{
	}

	[Fact]
	public void EnumsWithAssociatedConstants()
	{
		// Generate the enum that has a associated constant.
		GenerateApi("SERVICE_ERROR");
		EnumDeclarationSyntax enumDeclarationSyntax = Assert.IsType<EnumDeclarationSyntax>(FindGeneratedType("SERVICE_ERROR").Single());

		// The enum should contain the constant.
		Assert.Contains(enumDeclarationSyntax.Members, value => value.Identifier.ValueText == "SERVICE_NO_CHANGE");

		// The constant should not be generated as a separate constant.
		Assert.Empty(FindGeneratedConstant("SERVICE_NO_CHANGE"));
	}
}
