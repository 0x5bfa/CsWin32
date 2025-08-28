// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

internal record TypeSyntaxSettings(
	Generator? Generator,
	bool PreferNativeInt,
	bool PreferMarshaledTypes,
	bool AllowMarshaling,
	bool QualifyNames,
	bool IsField = false,
	bool PreferInOutRef = false,
	bool AvoidWinmdRootAlias = false);