# OBSharp.Interop.NativeBindGenerator

OBSharp.Interop.NativeBindGenerator 是一个基于 libclang 的命令行工具，可从 C 头文件中的函数和类型声明生成 C# P/Invoke 绑定。它也可以为 C++ 类生成 C 式导出包装，再为这些导出生成 C# 绑定。

生成的托管函数使用 .NET `LibraryImport`，支持根据声明生成调用约定、参数和返回值类型、结构体布局及不透明句柄。

## 环境要求

- .NET SDK 11（项目通过 [`global.json`](./global.json) 指定 SDK 版本）。
- 生成器解析头文件所需的 Clang 参数，例如包含目录、宏定义及语言标准。
- 使用 `--cpp-redirect` 时，还需使用与目标 C++ 库相匹配的编译器和构建配置编译生成的 native 文件。

## 安装与构建

本项目通过 GitHub Packages 发布 NuGet 工具包。首次安装前，先配置 OBSharp 的 GitHub Packages 源：

```shell
dotnet nuget add source "https://nuget.pkg.github.com/OBSharp/index.json" \
  --name OBSharp \
  --username <GitHub用户名> \
  --password <具有read:packages权限的PersonalAccessToken> \
  --store-password-in-clear-text
```

GitHub Packages 使用 Personal Access Token (classic) 进行 NuGet 源认证。请妥善保管令牌，不要将其提交到仓库。配置源后安装全局工具：

```shell
dotnet tool install --global OBSharp.Interop.NativeBindGenerator \
  --add-source "https://nuget.pkg.github.com/OBSharp/index.json"
```

如需安装指定版本，可追加 `--version <版本号>`。如果只想试用当前源码或尚未发布的版本，也可以在仓库根目录运行：

```shell
dotnet run --project src/OBSharp.Interop.NativeBindGenerator -- --help
```

## 基本用法

命令的基本格式如下：

```text
NativeBindGenerator [生成器选项] -- [clang 参数]
```

`--header` 至少需要指定一个头文件，可以重复指定。分隔符 `--` 后的参数会原样传递给 libclang；头文件解析所需的 `-I`、`-D` 和 `-std` 等参数应放在分隔符之后。

### 从 C 头文件生成 C# 绑定

假设 `native_api.h` 声明了可供外部调用的 C 函数：

```c
typedef struct Widget Widget;

Widget* widget_create(void);
void widget_destroy(Widget* widget);
int widget_get_value(const Widget* widget);
```

运行生成器：

```shell
NativeBindGenerator \
  --header native_api.h \
  --namespace MyApp.Interop \
  --library-name mynative \
  --csharp-output generated/csharp \
  -- \
  -I/path/to/native/include \
  -std=c17
```

生成器会在 `generated/csharp` 下写入 `NativeMethods.g.cs`，并在有相关类型时写入 `NativeTypes.g.cs`。将这些文件加入 C# 项目，并确保运行时能够加载 `--library-name` 指定的原生库。

### 为 C++ 类生成包装和 C# 绑定

使用 `--cpp-redirect` 可让生成器扫描头文件中的 C++ 类，并在 native 侧生成 C 式包装，再根据包装声明生成 C# 绑定：

```shell
NativeBindGenerator \
  --header widget.hpp \
  --namespace MyApp.Interop \
  --library-name mynative \
  --cpp-redirect \
  --native-output generated/native \
  --native-name widget_exports \
  --csharp-output generated/csharp \
  -- \
  -I/path/to/native/include \
  -std=c++20
```

此模式会在 `generated/native` 下生成：

- `widget_exports.h` 和 `widget_exports.cpp`：C 式导出声明与实现。
- `cpp_marshaller.hpp`：包装代码使用的类型转换和内存管理工具。

生成的 native 源码需要使用目标库对应的编译器、头文件、宏和链接设置构建成原生库。`--library-name` 应与最终加载的库名称相符。

### Qt 支持

对 Qt 项目，可附加 `--qt`（必须同时指定 `--cpp-redirect`）：

```shell
NativeBindGenerator \
  --header my_object.hpp \
  --cpp-redirect \
  --qt \
  --library-name myqtbridge \
  -- \
  -I/path/to/qt/include \
  -std=c++20
```

该选项会对 Qt 类型做专门处理，包括将信号转换为 `connect` 导出、通过 `deleteLater` 释放 `QObject`，以及在适用时转换 `QString` 与 UTF-8 字符串。请确保 Clang 参数与项目使用的 Qt 版本及构建配置一致。

## 生成器选项

| 选项 | 默认值 | 说明 |
| --- | --- | --- |
| `--header` | 无 | 要解析的头文件；可重复指定，至少需要一个。 |
| `--namespace`、`-n` | `OBSharp.Interop.Generated` | 生成的 C# 命名空间。 |
| `--library-name`、`-l` | `native` | `LibraryImport` 使用的原生库名。 |
| `--csharp-output` | `generated/csharp` | C# 文件输出目录。 |
| `--four-byte-boolean` | 关闭 | 将 `bool` 按 4 字节 `UnmanagedType.Bool` 封送；默认按 1 字节 `UnmanagedType.I1`。 |
| `--string-marshaller` | 默认 UTF-8 封送 | 为 `const char*` 参数指定自定义 marshaller 类型名。 |
| `--return-string-marshaller` | 默认 UTF-8 封送 | 为 `const char*` 返回值指定自定义 marshaller 类型名。 |
| `--cpp-redirect` | 关闭 | 生成 C++ 类的 C 式 native 转发代码，并据此生成 C# 绑定。 |
| `--qt` | 关闭 | 启用 Qt 特殊处理；需配合 `--cpp-redirect`。 |
| `--native-output` | `generated/native` | `--cpp-redirect` 的 native 文件输出目录。 |
| `--native-name` | `cpp_exports` | native 生成文件的基名，不含扩展名。 |

自定义 marshaller 参数只需填写类型名，例如 `MyStringMarshaller`；生成代码会使用 `MarshalUsing(typeof(MyStringMarshaller))`。自定义类型必须能被生成后的 C# 代码访问，并符合 .NET interop marshalling 的要求。

## 类型映射概览

### C 声明

- 常见 C 整数和浮点类型映射到对应的 C# 基本类型；`long` 和 `unsigned long` 分别映射为 `CLong` 和 `CULong`。
- `size_t`、`uintptr_t`、`ptrdiff_t`、`intptr_t` 及定宽整数类型会映射为相应的本机整数或 C# 整数类型。
- `const char*` 参数和返回值默认按 UTF-8 字符串处理；可分别通过 `--string-marshaller` 和 `--return-string-marshaller` 指定自定义 marshaller。
- `bool` 参数和返回值默认使用 1 字节封送；`--four-byte-boolean` 可切换为 4 字节封送。
- 函数指针生成为 `delegate* unmanaged`；数组以指针形式表示。
- 完整定义的结构体、联合体和枚举会生成对应 C# 类型。
- 不透明结构体指针可生成为 `SafeHandle`。符合常见释放函数形式的声明（例如接收单个不透明指针、返回 `void` 且函数名包含 `destroy`、`free`、`release`、`close` 或 `delete`）可用于关联句柄释放操作。

### C++ 转发

`--cpp-redirect` 会为头文件中的可处理 C++ 类和枚举生成 C 式导出。该模式支持常见标量、枚举、类对象和指针，以及部分字符串和 `std::vector` 转换；不支持的声明会被跳过或报告。具体可处理的类型取决于声明形式以及传入的 Clang 参数。

## 注意事项

- 生成器只分析指定头文件中的声明；包含目录、宏定义、平台宏和语言标准等配置应通过 `--` 传给 Clang。
- `--` 之后的参数会原样传递，不会由生成器自动补充。请为 C 头文件和 C++ 头文件分别提供合适的语言标准和编译配置。
- C# 绑定仅描述互操作声明，不会构建、复制或部署目标原生库。
- 生成器会清理 C# 输出目录中的旧 `*.g.cs` 文件后再写入新结果；请将该目录专用于生成文件。
- 遇到无法映射的类型或 Clang 解析错误时，请先检查头文件及其编译参数；部分不支持的类型可能会被跳过。

## 许可证

本项目使用 MIT 许可证，详见 [`LICENSE`](./LICENSE)。