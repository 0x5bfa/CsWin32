<h1 align="center">CsWin32Ex—A variant of CsWin32 optimized for unmarshalled scenarios</h1>
<p align="center">A streamlined fork of CsWin32 with a simplified codebase, enhanced usability for unmarshalled scenarios, and easier insights into how the source generator works internally.</p>

## Overview

CsWin32Ex introduces the smart pointers (i.e., `ComPtr`, `ComHeapPtr`, etc.) to simplify the lifetime management of COM objects in unmarshalled scenarios.
Additionally, all `Guid` properties now use RVA-backed fields, allowing you to take pointers directly without the need to copy them into local variables.

The codebase has been greatly simplified: unnecessary files have been removed, and a single GitHub Actions workflow has been set up to ensure code sustainability and testability.

```console
> dotnet add package CsWin32Ex --version 1.0.0
```

## Authoring CsWin32(Ex)-compatible WinMD files

When authoring a WinMD file for use with CsWin32 (or CsWin32Ex), ensure that you follow the requirements outlined below.

Although these requirements ideally should not exist, maintaining compatibility with existing WinMD files supported by upstream CsWin32 takes precedence.

### Native handles

To generate a proper dispose method for a native handle, define a `RAIIFreeAttribute` with the method name as its first parameter, and apply it to the structs representing native handles (the generator only checks for the attribute’s name; its origin does not matter).

```cs
public class RAIIFreeAttribute(string Name) : Attribute {}
```

```cs
[RAIIFree("CloseHandle")]
public struct HANDLE
{
    // When this is IntPtr or UIntPtr, an appropriate SafeHandle will also be generated.
    public unsafe void* Value;
}

```

_More coming soon._
