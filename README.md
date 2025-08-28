<h1 align="center">CsWin32Ex—A variant of CsWin32 optimized for unmarshalled scenarios</h1>
<p align="center">A streamlined fork of CsWin32 with a simplified codebase, enhanced usability for unmarshalled scenarios, and easier insights into how the source generator works internally.</p>

## Overview

CsWin32Ex introduces the smart pointers (i.e., `ComPtr`, `ComHeapPtr`, etc.) to simplify the lifetime management of COM objects in unmarshalled scenarios.
Additionally, all `Guid` properties now use RVA-backed fields, allowing you to take pointers directly without the need to copy them into local variables.

The codebase has been greatly simplified: unnecessary files have been removed, and a single GitHub Actions workflow has been set up to ensure code sustainability and testability.

## Usage

```console
> dotnet add package CsWin32Ex --version 1.0.0
```

## External WinMD file inputs

_Coming soon._
