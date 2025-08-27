// Copyright (c) 0x5BFA.

namespace Files.CsWin32;

internal record TypeSyntaxSettings(
	Generator? Generator,
	bool PreferNativeInt,
	bool PreferMarshaledTypes,
	bool AllowMarshaling,
	bool QualifyNames,
	bool IsField = false,
	bool PreferInOutRef = false,
	bool AvoidWinmdRootAlias = false);