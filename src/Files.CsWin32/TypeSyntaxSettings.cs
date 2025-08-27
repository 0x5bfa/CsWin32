// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Files.CsWin32;

internal record TypeSyntaxSettings(
    Generator? Generator,
    bool PreferNativeInt,
    bool PreferMarshaledTypes,
    bool AllowMarshaling,
    bool QualifyNames,
    bool IsField = false,
    bool PreferInOutRef = false,
    bool AvoidWinmdRootAlias = false)
{
}
