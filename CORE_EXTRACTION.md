# Core Extraction Task Brief

> **Dành cho session đang xử lý RevitMCPServer.**
> Đây là hướng dẫn chi tiết để tách `RevitMCP.Core` classlib từ repo hiện tại.
> Không cần hiểu repo RevitAssistant — chỉ cần làm đúng các bước dưới đây trong repo RevitMCPServer.

---

## Mục tiêu

Tạo project `RevitMCP.Core` mới trong repo RevitMCPServer, chuyển toàn bộ service layer vào đó, và update `RevitMCPAddin` để reference vào Core. Kết quả: `RevitMCP.Core` sẽ được dùng làm **git submodule** trong repo RevitAssistant (Phase 1 của RevitAssistant).

---

## Context hiện tại (đã verify ngày 2026-06-15)

Repo: `C:\Users\lep\My Drive\02 RD Projects\00 AI\RevitMCPServer`  
Git HEAD: `e6b00b8` (v0.8.0)

Cấu trúc hiện tại:
```
src/
  RevitAddin/
    App.cs                          ← GIỮ NGUYÊN trong RevitMCPAddin
    Server/McpHttpServer.cs         ← GIỮ NGUYÊN trong RevitMCPAddin
    RevitMCPAddin.csproj            ← UPDATE (thêm ProjectReference Core)
    RevitMCPAddin.addin             ← GIỮ NGUYÊN
    RevitMCPExternalEventHandler.cs ← MOVE sang Core
    Commands/                       ← MOVE toàn bộ sang Core
      IRevitCommand.cs
      CommandContext.cs
      CommandRegistry.cs
      JsonResult.cs
      RevitCommandException.cs
      ParamUtil.cs
      UnitConversionPolicy.cs
      BatchPolicy.cs
      [tất cả ~70 command files *.cs]
  RevitAddin.Tests/
    *.cs                            ← UPDATE using statements nếu cần
```

---

## Các bước thực hiện

### Bước 1 — Tạo branch mới

```bash
cd "C:\Users\lep\My Drive\02 RD Projects\00 AI\RevitMCPServer"
git checkout -b feat/extract-revit-mcp-core
```

### Bước 2 — Tạo project `RevitMCP.Core`

Tạo file `src/RevitMCP.Core/RevitMCP.Core.csproj` với nội dung:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <AssemblyName>RevitMCP.Core</AssemblyName>
    <RootNamespace>RevitMCPAddin</RootNamespace>
    <!-- GIỮ namespace RevitMCPAddin.Commands để KHÔNG cần đổi using statements -->
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <RevitVersion Condition="'$(RevitVersion)' == ''">2026</RevitVersion>
    <RevitInstallDir Condition="'$(RevitInstallDir)' == ''">C:\Program Files\Autodesk\Revit $(RevitVersion)</RevitInstallDir>
    <TargetFramework Condition="$([MSBuild]::VersionGreaterThanOrEquals($(RevitVersion), 2027))">net10.0-windows</TargetFramework>
    <TargetFramework Condition="$([MSBuild]::VersionLessThan($(RevitVersion), 2027))">net8.0-windows</TargetFramework>
  </PropertyGroup>

  <!-- Dev machine: real Revit DLLs -->
  <ItemGroup Condition="Exists('$(RevitInstallDir)\RevitAPI.dll')">
    <Reference Include="RevitAPI">
      <HintPath>$(RevitInstallDir)\RevitAPI.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="RevitAPIUI">
      <HintPath>$(RevitInstallDir)\RevitAPIUI.dll</HintPath>
      <Private>False</Private>
    </Reference>
  </ItemGroup>

  <!-- CI fallback: Nice3point -->
  <ItemGroup Condition="!Exists('$(RevitInstallDir)\RevitAPI.dll') And '$(RevitVersion)' == '2025'">
    <PackageReference Include="Nice3point.Revit.Api.RevitAPI" Version="2025.2.0">
      <PrivateAssets>all</PrivateAssets><IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPIUI" Version="2025.2.0">
      <PrivateAssets>all</PrivateAssets><IncludeAssets>compile</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup Condition="!Exists('$(RevitInstallDir)\RevitAPI.dll') And '$(RevitVersion)' == '2026'">
    <PackageReference Include="Nice3point.Revit.Api.RevitAPI" Version="2026.4.10">
      <PrivateAssets>all</PrivateAssets><IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPIUI" Version="2026.4.10">
      <PrivateAssets>all</PrivateAssets><IncludeAssets>compile</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup Condition="!Exists('$(RevitInstallDir)\RevitAPI.dll') And '$(RevitVersion)' == '2027'">
    <PackageReference Include="Nice3point.Revit.Api.RevitAPI" Version="2027.0.20">
      <PrivateAssets>all</PrivateAssets><IncludeAssets>compile</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Nice3point.Revit.Api.RevitAPIUI" Version="2027.0.20">
      <PrivateAssets>all</PrivateAssets><IncludeAssets>compile</IncludeAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

> **Lưu ý RootNamespace:** giữ `RevitMCPAddin` (không phải `RevitMCP.Core`) để files `.cs` được move sang không cần đổi namespace declaration.

### Bước 3 — Move files vào Core

**Di chuyển** (git mv, không copy) toàn bộ các files sau:

```bash
# Di chuyển toàn bộ Commands directory
git mv src/RevitAddin/Commands src/RevitMCP.Core/Commands

# Di chuyển ExternalEventHandler
git mv src/RevitAddin/RevitMCPExternalEventHandler.cs src/RevitMCP.Core/
```

**Kết quả mong muốn:**
```
src/RevitMCP.Core/
  RevitMCP.Core.csproj
  RevitMCPExternalEventHandler.cs
  Commands/
    IRevitCommand.cs
    CommandContext.cs
    CommandRegistry.cs
    JsonResult.cs
    RevitCommandException.cs
    ParamUtil.cs
    UnitConversionPolicy.cs
    BatchPolicy.cs
    [tất cả command files...]

src/RevitAddin/
  App.cs                    ← GIỮ NGUYÊN
  Server/McpHttpServer.cs   ← GIỮ NGUYÊN
  RevitMCPAddin.csproj      ← sẽ UPDATE ở bước 4
  RevitMCPAddin.addin       ← GIỮ NGUYÊN
  [không còn Commands/ nữa]
```

### Bước 4 — Update RevitMCPAddin.csproj

Mở `src/RevitAddin/RevitMCPAddin.csproj`, thêm ProjectReference vào Core:

```xml
<ItemGroup>
  <ProjectReference Include="..\RevitMCP.Core\RevitMCP.Core.csproj" />
</ItemGroup>
```

Không cần xóa gì — Revit API references giữ nguyên (App.cs và McpHttpServer.cs vẫn cần).

### Bước 5 — Update InternalsVisibleTo

Trong file `src/RevitAddin/Server/McpHttpServer.cs`, dòng đầu có:
```csharp
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RevitMCPAddin.Tests")]
```
Giữ nguyên — tests vẫn reference `RevitMCPAddin.Tests`.

### Bước 6 — Thêm vào solution

```bash
cd "C:\Users\lep\My Drive\02 RD Projects\00 AI\RevitMCPServer"
dotnet sln add src/RevitMCP.Core/RevitMCP.Core.csproj
```

### Bước 7 — Build + Test

```bash
dotnet build -p:RevitVersion=2026 -p:DeployToRevit=false
dotnet test -p:RevitVersion=2026 -p:DeployToRevit=false
```

**Kỳ vọng:** tất cả tests PASS. Build SUCCESS. Nếu có lỗi:
- `CS0246 type not found` → check xem file đã được move đúng chưa
- `CS0234 namespace not found` → check `RootNamespace` trong Core.csproj có đúng là `RevitMCPAddin` không
- Missing `using` → thêm `using RevitMCPAddin.Commands;` vào file cần

### Bước 8 — Commit

```bash
git add -A
git commit -m "feat: extract RevitMCP.Core classlib from RevitMCPAddin

Moves Commands/ + RevitMCPExternalEventHandler.cs into a new
RevitMCP.Core classlib so RevitAssistant can consume it as a
git submodule without duplicating the service layer.

Namespace unchanged (RevitMCPAddin.Commands) to minimize diff.
RevitMCPAddin references Core via ProjectReference.
All existing tests pass."
```

---

## Output mong muốn

Sau khi bước này hoàn tất, repo RevitMCPServer có:
- Branch `feat/extract-revit-mcp-core` (hoặc merge vào main nếu muốn)
- Project `src/RevitMCP.Core/RevitMCP.Core.csproj` chứa toàn bộ service layer
- `RevitMCPAddin.csproj` references Core
- Tất cả tests pass
- Build pass cho R26 (và R25, R27 nếu test)

## Sau khi xong → báo lại cho RevitAssistant session

RevitAssistant Phase 1 sẽ:
1. `git submodule add <RevitMCPServer-url> extern/RevitMCP.Core`
2. Xóa `src/RevitAssistant.Core/CorePlaceholder.cs`
3. Update `RevitAssistant.Core.csproj` → thành wrapper project hoặc trực tiếp reference submodule
4. Build + verify

---

## Không cần làm

- Không cần đổi namespace (giữ `RevitMCPAddin.Commands`)
- Không cần xóa hay sửa `App.cs`
- Không cần xóa hay sửa `McpHttpServer.cs`
- Không cần đụng vào `RevitMCPAddin.addin`
- Không cần push lên GitHub (chỉ commit local là đủ để làm submodule)
