<h1 align="center">CsWin32Ex—A variant of CsWin32 optimized for unmarshalled scenarios</h1>
<p align="center">A streamlined fork of CsWin32 with a simplified codebase, enhanced usability for unmarshalled scenarios, and easier insights into how the source generator works internally.</p>

## Overview

CsWin32Ex introduces the smart pointers (i.e., `ComPtr`, `ComHeapPtr`, etc.) to simplify the lifetime management of COM objects in unmarshalled scenarios.
Additionally, all `Guid` properties now use RVA-backed fields, allowing you to take pointers directly without the need to copy them into local variables.

The codebase has been greatly simplified: unnecessary files have been removed, and a single GitHub Actions workflow has been set up to ensure code sustainability and testability.

```bash
> dotnet add package CsWin32Ex --version 1.0.0
```

## Authoring CsWin32(Ex)-compatible WinMD files

When authoring a WinMD file for use with CsWin32 (or CsWin32Ex), ensure that you follow the requirements outlined below.

### Supported architectures & OS platforms

To prevent the generator from generating a type that is not compatible with the current platforms and the OS platform configured in the project that consumes the generator and to report a diagnostic or an error for it, define a `SupportedArchitectureAttribute` & `SupportedOSPlatform` with an enum as its first parameter, and apply it to the type.

```cs
[Flags]
public enum Architecture
{
    None = 0,
    X86 = 1,
    X64 = 2,
    Arm64 = 4,
    All = Architecture.X64 | Architecture.X86 | Architecture.Arm64
}

public class SupportedArchitectureAttribute(Architecture Architecture) : Attribute { }

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = false)]
public class SupportedOSPlatformAttribute(string OSPlatform) : Attribute { }
```

```cs
[DllImport(...)]
[SupportedArchitecture(Windows.Win32.Foundation.Metadata.Architecture.X64 | Windows.Win32.Foundation.Metadata.Architecture.Arm64)]
[SupportedOSPlatform("windows5.0")]
public static extern IntPtr SetWindowLongPtrW(...);
```

### Native handles

To generate a proper dispose method for a native handle, define a `RAIIFreeAttribute` with the method name as its first parameter, and apply it to the structs representing native handles (the generator only checks for the attribute’s name; its origin does not matter).

```cs
public class RAIIFreeAttribute(string MethodName) : Attribute {}
```

```cs
[RAIIFree("CloseHandle")]
public struct HANDLE
{
    // When this is IntPtr or UIntPtr, an appropriate SafeHandle will also be generated.
    public unsafe void* Value;
}
```

### Enum-associated constants

To generate constants associated to an enum type, define `AssociatedConstantAttribute` with the const name as its first parameter, and apply it to the enum. The associated constants will be generated inside the enum and will not be generated as a constant.

```cs
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = true)]
public class AssociatedConstantAttribute(string ConstName) : Attribute { }
```

```cs
[AssociatedConstant("SERVICE_NO_CHANGE")]
public enum SERVICE_ERROR : uint
{
	...
    // "SERVICE_NO_CHANGE = 4294967295U" will be generated
}
```

_More coming soon._
