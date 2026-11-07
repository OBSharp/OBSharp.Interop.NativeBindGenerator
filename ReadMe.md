# OBSharp.Interop.NativeBindGenerator

OBSharp.Interop.NativeBindGenerator 是一个基于 libclang 的命令行工具，可从 C 头文件中的函数和类型声明生成 C# P/Invoke 绑定。它也可以为 C++ 类生成 C 式导出包装，再为这些导出生成 C# 绑定。

生成的托管函数使用 .NET `LibraryImport`，支持根据声明生成调用约定、参数和返回值类型、结构体布局及不透明句柄。
生成的 `LibraryImport` 方法为 `internal`；完整的 C/C++ 声明会记录在生成代码的 XML 文档注释中。

## 环境要求

- .NET SDK 11（项目通过 [`global.json`](./global.json) 指定 SDK 版本）。
- 生成器解析头文件所需的 Clang 参数，例如包含目录、宏定义及语言标准。
- 使用 `--cpp-redirect` 时，还需使用与目标 C++ 库相匹配的编译器和构建配置编译生成的 native 文件。
- 发布包按运行时标识（RID）分发：主包只包含工具清单，实际工具与 `libclang`、`libClangSharp` 原生库位于各 RID 子包中，安装时按当前平台自动选择，无需另行安装 Clang 或复制原生库。

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

发布时需要同时推送主包和全部 RID 子包（`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-arm64`），否则安装时会缺少对应平台的原生库。

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

生成器会在 `generated/csharp` 下按声明拆分为多个 `*.g.cs` 文件（均是统一命名空间下的 partial 声明）：

- 能按名称前缀与首参类型归类到某个类型的函数，其 `LibraryImport` 声明、全局包装和成员方法都写入该类型的 `*.g.cs` 文件，并放在同一个 partial 类型里；例如 `widget_get_value` 会进入 `Widget` 类型，调用处无需再经过 `NativeMethods`。
- `get_`/`set_` 成对的函数合并为属性；句柄类型的成员只有一次转发调用，因此生成表达式主体（`get => ...;`、`set => ...;`），只有 getter 时进一步简化为 `public int Value => Native_get_value(this);`。方法同样使用 `=>`，例如 `public int Compute(int a, int b) => Native_compute(this, a, b);`。
- 需要先通过 `fixed` 取得自身指针的结构体实例成员无法写成表达式主体，这类成员保留 `{ ... }` 块主体。
- 返回某个句柄类型的工厂函数（如 `Widget * Widget_create(int)`、`OpaqueContext * opaque_create(void)`）也会进入该类型，并生成静态方法而不是构造函数，例如 `Widget.Create(42)`、`OpaqueContext.OpaqueCreate()`。
- 只有当函数无法归类（没有对应的类型前缀、首参不属于任何已生成类型，且返回类型也不是句柄类型）时，才作为全局函数写入 `NativeMethods.g.cs`；数量较多时继续拆成 `NativeMethods.2.g.cs`、`NativeMethods.3.g.cs` 等 partial 分片。
- 每个结构体、联合体、枚举、SafeHandle 以及定长数组的 `InlineArray` 类型各写入一个以类型名命名的 `*.g.cs` 文件；类型定义、成员扩展与导入声明写在同一个文件中。

分散文件可让 Roslyn 并行解析并按文件增量编译，避免单个巨大文件拖慢编译。将这些文件加入 C# 项目，并确保运行时能够加载 `--library-name` 指定的原生库。

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

此模式会在 `generated/native` 下按类拆分为多个文件：

- `<native-name>.h`：公共头文件，含桥接类型、枚举、不透明指针句柄以及全部类句柄的前置声明，由所有拆分文件包含。
- `<native-name>.pch.h`：预编译头候选文件，只包含全部 `--header` 原始头文件，顺序与每个类 `.cpp` 的 preamble 一致；可直接交给 CMake 等构建系统生成 PCH。
- `<native-name>.cpp`：公共实现，仅含 `free_memory`（以及启用 `--qt` 时的连接销毁）。
- `<类名>.h` 和 `<类名>.cpp`：每个类各一对，分别声明和实现该类的全部 C 式导出。类名取 C 名（命名空间以 `_` 连接，如 `demo_Widget`），非法字符替换为 `_`。
- `cpp_marshaller.hpp`：包装代码使用的类型转换和内存管理工具。
- `native_manifest.txt`：本次生成的文件清单，供下次运行清理过期产物。
- `generated-libraries.cmake`：库划分清单（仅当导出数超过单库上限时才有实际意义），供 CMake 为每个库建立独立目标。

生成的 native 源码需要使用目标库对应的编译器、头文件、宏和链接设置构建成原生库。`--library-name` 应与最终加载的库名称相符。编译时把该目录下的所有 `*.cpp` 一并加入即可。

按类拆分是为了避免单个超大翻译单元：类多、方法多时，一个巨大 `.cpp` 会触发编译器的对象文件段数、内存或编译时间上限，甚至直接编译失败。拆分后每个 `.cpp` 只包含自己那一对头文件与原始头文件，既能并行编译，也便于按类增量构建与定位错误。代价是每个翻译单元都要重新解析一遍原始头文件，因此对少量类的头文件，单文件 unity 构建可能仍更快；验证阶段可以先用 `<native-name>.pch.h` 生成预编译头，或用 `--batch-size` 合并翻译单元，确认稳定后再评估 `--minimal-includes`。

> 注意：拆分出的 `<类名>.cpp` 不包含同名 `.h`。头文件里的句柄参数是 `demo_Widget*` 这类强类型指针，而实现统一用 `void*` 承接；同名 `extern "C"` 函数若两处签名不同会直接编译报错，因此头文件只供使用方和 C# 绑定生成读取。

#### 单个库放不下时自动拆成多个库

一个 PE/COFF DLL 的导出表最多容纳 **65,535** 项，超过后链接器直接失败：

```text
LINK : fatal error LNK1189: 超过 65535 对象的库限制
```

像 Qt 这样的大项目经 `--cpp-redirect --qt` 展开后会产生 7 万以上导出，单个库必然放不下。此时生成器会把类按容量贪心装箱，拆成多个库：

- **主库**沿用 `--library-name` 的名字（如 `OBSharp.Interop.Qt.Native`）；
- **附属库**依次追加 `1`、`2`、`3`… 后缀（如 `OBSharp.Interop.Qt.Native1`）；
- 主库的源码仍在 native 产物根目录，附属库的源码位于 `<库名>/` 子目录；
- 每个类整体属于一个库，不会被拆开；
- 生成的 C# 绑定里，每个 `LibraryImport` 指向真正包含该函数的那个库，调用方无需区分。

控制台会打印实际划分，例如：

```text
生成了 73546 个 C 式导出、1867 个 native 文件（.../native）。
导出数超过单个库上限 65535，已拆成 2 个原生库：
  OBSharp.Interop.Qt.Native: 46732 个导出、612 个类（主库）
  OBSharp.Interop.Qt.Native1: 26814 个导出、321 个类
```

需要在 CMake 侧为每个库建立独立目标时，消费 `generated-libraries.cmake`：

```cmake
include("${GEN_DIR}/generated-libraries.cmake")   # 定义 OBSHARP_NATIVE_LIBRARIES

foreach(library IN LISTS OBSHARP_NATIVE_LIBRARIES)
    string(TOUPPER "${library}" key)
    string(REGEX REPLACE "[^A-Z0-9]" "_" key "${key}")
    set(source_dir "${OBSHARP_NATIVE_LIBRARY_${key}_SOURCE_DIR}")

    # 每个库只收自己目录下的 .cpp：主库在根目录，附属库在各自子目录，
    # 因此这里用 GLOB 而不是 GLOB_RECURSE。
    file(GLOB sources CONFIGURE_DEPENDS "${source_dir}/*.cpp")
    add_library(${library} SHARED ${sources})
    target_compile_features(${library} PRIVATE cxx_std_20)
    if(MSVC)
        target_compile_options(${library} PRIVATE /utf-8)
    endif()
    target_link_libraries(${library} PRIVATE ${qt_targets})
endforeach()
```

单库上限可用环境变量调整（主要用于测试）：

```shell
OBSHARP_MAX_EXPORTS_PER_LIBRARY=20000
```

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
| `--nullable-handle-marshaller` | 默认关闭 | 为句柄参数指定可空 marshaller 类型名；指定后句柄参数生成 `Xxx?`，托管 `null` 映射为原生 NULL。 |
| `--cpp-redirect` | 关闭 | 生成 C++ 类的 C 式 native 转发代码，并据此生成 C# 绑定。 |
| `--qt` | 关闭 | 启用 Qt 特殊处理；需配合 `--cpp-redirect`。 |
| `--native-output` | `generated/native` | `--cpp-redirect` 的 native 文件输出目录。 |
| `--native-name` | `cpp_exports` | native 公共文件（公共头/公共实现/预编译头）的基名；各类按类名单独命名。 |
| `--minimal-includes` | 关闭 | 每个类的 `.cpp` 只包含该类的导出实际用到的原始头文件，而不是全部 `--header`。可显著缩短单个翻译单元的解析时间；建议逐平台验证编译与链接后再启用。 |
| `--relative-includes` | 关闭 | 把 `--header` 写成相对 native 输出目录的路径（附属库相对各自子目录），便于 ccache/sccache 跨机器复用。 |
| `--batch-size` | `1` | 每 N 个类合并为一个 `.cpp`，减少编译器进程数与 PCH 加载次数；类头文件仍逐个生成。 |

自定义 marshaller 参数只需填写类型名，例如 `MyStringMarshaller`；生成代码会使用 `MarshalUsing(typeof(MyStringMarshaller))`。自定义类型必须能被生成后的 C# 代码访问，并符合 .NET interop marshalling 的要求。

`--nullable-handle-marshaller` 遵循同样的填写方式。指定后，句柄参数在包装 API 中变成 `Xxx?`，导入声明则使用 `[MarshalUsing(typeof(该类型))] SafeHandle?`，这样同一个 marshaller 可以覆盖所有生成的句柄类型；marshaller 需按 `[CustomMarshaller(typeof(SafeHandle), MarshalMode.ManagedToUnmanagedIn, typeof(...))]` 声明。

## 类型映射概览

### C 声明

- 常见 C 整数和浮点类型映射到对应的 C# 基本类型；`long` 和 `unsigned long` 分别映射为 `CLong` 和 `CULong`。
- `size_t` 和 `uintptr_t` 映射为随进程位数变化的 `nuint`；`ptrdiff_t`、`ssize_t` 和 `intptr_t` 映射为 `nint`。定宽整数按原生符号性映射为对应的 C# 整数类型；普通 `char` 映射为 `byte`，`signed char` 映射为 `sbyte`。
- `const char*` 参数和返回值默认按 UTF-8 字符串处理；可分别通过 `--string-marshaller` 和 `--return-string-marshaller` 指定自定义 marshaller。
- `bool` 参数和返回值默认使用 1 字节封送；`--four-byte-boolean` 可切换为 4 字节封送。
- 函数指针生成为 `delegate* unmanaged`；数组以指针形式表示。
- 完整定义的结构体、联合体和枚举会生成对应 C# 类型；结构体中的定长数组使用 `InlineArray`。
- 对象及不透明结构体指针可生成为 `SafeHandle`。符合常见释放函数形式的声明（例如接收单个不透明指针、返回 `void` 且函数名包含 `destroy`、`free`、`release`、`close` 或 `delete`）可用于关联句柄释放操作。句柄参数默认生成 `Xxx`；指定 `--nullable-handle-marshaller` 后生成 `Xxx?`，允许把托管 `null` 传给原生 NULL。

### C++ 转发

`--cpp-redirect` 会为头文件中的可处理 C++ 类和枚举生成 C 式导出。该模式支持常见标量、枚举、类对象和指针，以及部分字符串和 `std::vector` 转换。指向未在指定头文件中发现定义的 C++ 类的指针会回退为 `nint`，不会仅因指针类型无法细化映射而跳过。模板特化指针使用 C# 泛型不透明结构体（例如 `QList<nint>*`），避免把 C++ 模板拼写直接当作 C# 标识符；跨模块的不透明指针不会重复声明冲突的托管类型。具体可处理的类型取决于声明形式以及传入的 Clang 参数。

启用 `--qt` 时，自动数组转换支持元素为 C 基本类型或指针的 `QList<T>`；托管 API 使用 `ReadOnlySpan<T>`，指针元素以 `nint` 表示，native 边界使用元素指针与 `size_t`，并在 C++ 包装中转换为 `QList<T>`。返回值会复制到托管内存后以只读跨度返回。其他复杂值类型的 `QList<T>` 尚未支持自动跨度转换。`size_t`、`uintptr_t` 等无符号原生宽度整数映射为 `nuint`；`ptrdiff_t` 等有符号原生宽度整数映射为 `nint`，宽度均随目标平台变化。

## 注意事项

- 生成器只分析指定头文件中的声明；包含目录、宏定义、平台宏和语言标准等配置应通过 `--` 传给 Clang。
- `--` 之后的参数会原样传递，不会由生成器自动补充。请为 C 头文件和 C++ 头文件分别提供合适的语言标准和编译配置。
- C# 绑定仅描述互操作声明，不会构建、复制或部署目标原生库。
- 生成器会清理 C# 输出目录中的旧 `*.g.cs` 文件后再写入新结果；请将该目录专用于生成文件。
- 遇到无法映射的类型或 Clang 解析错误时，请先检查头文件及其编译参数；部分不支持的类型可能会被跳过。

## 许可证

本项目使用 MIT 许可证，详见 [`LICENSE`](./LICENSE)。