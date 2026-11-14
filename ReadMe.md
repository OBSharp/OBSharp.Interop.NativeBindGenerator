# OBSharp.Interop.NativeBindGenerator

OBSharp.Interop.NativeBindGenerator 是一个基于 libclang 的命令行工具，可从 C 头文件中的函数和类型声明生成 C# P/Invoke 绑定。它也可以为 C++ 类生成 C 式导出包装，再为这些导出生成 C# 绑定。

生成的托管函数使用 .NET `LibraryImport`，支持根据声明生成调用约定、参数和返回值类型、结构体布局及不透明句柄。
生成的 `LibraryImport` 方法为 `internal`；完整的 C/C++ 声明会记录在生成代码的 XML 文档注释中。

## 环境要求

- .NET SDK 11（项目通过 [`global.json`](./global.json) 指定 SDK 版本）。
- 生成器解析头文件所需的 Clang 参数，例如包含目录、宏定义及语言标准。
- 使用 `cpp` / `qt` 子命令时，还需使用与目标 C++ 库相匹配的编译器和构建配置编译生成的 native 文件。
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

工具按生成模式提供三个子命令，子命令必须是第一个参数：

```text
NativeBindGenerator <c|cpp|qt> [生成器选项] -- [clang 参数]
```

| 子命令 | 用途 |
| --- | --- |
| `c` | 直接解析 C 头文件生成 C# 绑定，不产出 native shim；`LibraryImport` 直接指向被绑定的库。 |
| `cpp` | 为 C++ 类生成 `extern "C"` 导出层，重新解析该导出层后再生成 C# 绑定。 |
| `qt` | 在 `cpp` 的基础上追加 Qt 特化（信号转连接导出、`QString` 走 UTF-8 借用、`QObject` 用 `deleteLater` 释放）。 |

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
NativeBindGenerator c \
  --header native_api.h \
  --namespace MyApp.Interop \
  --library-name mynative \
  --csharp-output generated/csharp \
  -- \
  -I/path/to/native/include \
  -std=c17
```

生成器会在 `generated/csharp` 下按声明拆分为多个文件（均是统一命名空间下的 partial 声明）：

- `<类型名>.g.d.cs`：阶段 1 的类型定义，每个结构体、联合体、枚举、`SafeHandle` 与定长数组 `InlineArray` 各一个文件。
- `<类型名>.g.n.<序号>.cs`：阶段 2 的 `LibraryImport` 原生导入声明。能按名称前缀与首参类型归类到某个类型的函数，其导入声明写入该类型的分片文件；无法归类的函数写入 `NativeMethods.g.n.<序号>.cs`。
- `<类型名>.g.<序号>.cs`：阶段 3 的托管封装（方法、属性与构造函数），按 `--methods-per-file` 分片。
- `<类型名>.g.c.<序号>.cs`：阶段 4 的常量。

`get_`/`set_` 成对的函数合并为属性；句柄类型的成员只有一次转发调用，因此生成表达式主体（`get => ...;`、`set => ...;`），只有 getter 时进一步简化为 `public int Value => Native_get_value(this);`。方法同样使用 `=>`。需要先通过 `fixed` 取得自身指针的结构体实例成员无法写成表达式主体，这类成员保留 `{ ... }` 块主体。

分散文件可让 Roslyn 并行解析并按文件增量编译，避免单个巨大文件拖慢编译。将这些文件加入 C# 项目，并确保运行时能够加载 `--library-name` 指定的原生库。

### 为 C++ 类生成包装和 C# 绑定

```shell
NativeBindGenerator cpp \
  --header widget.hpp \
  --namespace MyApp.Interop \
  --library-name mynative \
  --native-output generated/native \
  --native-name widget_shim \
  --csharp-output generated/csharp \
  -- \
  -I/path/to/native/include \
  -std=c++20
```

此模式在 `--native-output` 目录下产出下列文件（所有 `*.cpp` 都位于该目录的根目录，文件名自带库序号）：

- `<native-name>.h`：导出头文件。开头先输出**统一的句柄前置声明块**（`typedef struct <句柄名> <句柄名>;`，按名字去重、覆盖参数与返回值里出现的全部句柄，含嵌套类型与模板实例），再按类输出注释化的句柄声明，最后是全部导出函数声明。由所有实现文件包含。
- `<native-name>.pch.h`：预编译头候选文件，只包含全部 `--header` 原始头文件；可直接交给 CMake 等构建系统生成 PCH。默认写绝对路径，开启 `--relative-includes` 后改写为相对 `--native-output` 的路径。
- `<native-name>.<序号>.cpp`（单库）或 `<native-name>.<库序号>.<源序号>.cpp`（多库）：导出函数的实现，按 `--max-exports-per-source` 切分。
- `<native-name>.cmake`：自包含的 CMake 清单，每个库一段 `add_library(...)` + `target_include_directories` + `OUTPUT_NAME`，并额外提供显式库列表变量（见下）。
- `cpp_shim_marshal.hpp`：C 侧的通用类型转换与内存管理工具（`qt` 子命令另产出 `qt_shim_marshal.hpp`）。
- `cpp_shim_libraries.txt`：每行「库名 + TAB + 导出符号」，供生成 C# 绑定时为每个函数写入正确的 `LibraryImport` 库名。

生成的 native 源码需要使用目标库对应的编译器、头文件、宏和链接设置构建成原生库。`--library-name` 应与最终加载的库名称相符。

> **固定名说明（P1-3）**：`cpp_shim_marshal.hpp`、`cpp_shim_libraries.txt`、`qt_shim_marshal.hpp` 三个文件名，以及导出宏 `CPP_SHIM_EXPORT` / `CPP_SHIM_CALL`，都是**固定名**，不随 `--native-name` 或 `--export-prefix` 变化；只有 `<native-name>` 系列与句柄前缀跟随参数。之所以保持固定，是因为它们以嵌入资源形式随工具分发、并与 Qt 侧已有的 CMake 配置兼容。

#### 单个库放不下时自动拆成多个库

一个 PE/COFF DLL 的导出表最多容纳 **65,535** 项，超过后链接器直接失败：

```text
LINK : fatal error LNK1189: 超过 65535 对象的库限制
```

导出数超过 `--max-exports-per-library`（默认 `65535`）时，生成器按容量贪心装箱把导出拆到多个库：

- **第一个库**沿用 `--native-name`（同时是 CMake 目标名）；
- **后续库**依次追加 `1`、`2`… 后缀；
- **所有** `.cpp` 都在 native 产物根目录，靠文件名里的库序号区分归属，因此不要用目录 GLOB 判断库归属；
- 生成的 C# 绑定里，每个 `LibraryImport` 指向真正包含该函数的那个库，调用方无需区分。

清单里会输出一个显式库列表变量，供下游直接接入：变量名为 `<目标名转大写、非字母数字字符替换为下划线>_LIBRARIES`。例如 `--native-name OBSharp.Interop.Qt.Native` 产出：

```cmake
# <native-name>.cmake 片段
set(OBSHARP_INTEROP_QT_NATIVE_LIBRARIES
    "OBSharp.Interop.Qt.Native"
    "OBSharp.Interop.Qt.Native1"
)
```

> 注意：目标名允许 `.`（如 `OBSharp.Interop.Qt.Native`），但列表变量名一律规范化成字母数字与下划线，避免带点号的变量名在 `${}` / `LISTS` 中与 CMake 组件语法混淆。
>
> 也可以直接遍历清单建立的目标，无需依赖列表变量：
>
> ```cmake
> get_directory_property(libraries BUILDSYSTEM_TARGETS)
> ```

完整片段：

```cmake
set(OBSHARP_INTEROP_QT_NATIVE_LIBRARIES
    "OBSharp.Interop.Qt.Native"
    "OBSharp.Interop.Qt.Native1"
)

set(CPP_SHIM_SOURCES_1
    "${CMAKE_CURRENT_LIST_DIR}/OBSharp.Interop.Qt.Native.1.1.cpp"
)
add_library(OBSharp.Interop.Qt.Native SHARED ${CPP_SHIM_SOURCES_1})
target_include_directories(OBSharp.Interop.Qt.Native PUBLIC "${CMAKE_CURRENT_LIST_DIR}")
target_compile_features(OBSharp.Interop.Qt.Native PUBLIC cxx_std_17)
set_target_properties(OBSharp.Interop.Qt.Native PROPERTIES OUTPUT_NAME "OBSharp.Interop.Qt.Native")
```

下游可这样消费：

```cmake
include("${OBSHARP_NATIVE_MANIFEST}")   # 指向 <native-name>.cmake

foreach(library IN LISTS OBSHARP_INTEROP_QT_NATIVE_LIBRARIES)
    target_compile_features(${library} PRIVATE cxx_std_20)
    target_compile_options(${library} PRIVATE /utf-8)
    target_link_libraries(${library} PRIVATE ${qt_targets})
endforeach()
```

> 注意：`<native-name>.cmake` 这个文件名与自包含形态是 alpha.7 起的变化，alpha.5 的 `generated-libraries.cmake`（变量式 `OBSHARP_NATIVE_LIBRARIES` + 每库 `_SOURCE_DIR`）**已被移除**，迁移时请改用上面的清单 + 库列表变量。

### Qt 支持

对 Qt 项目使用 `qt` 子命令：

```shell
NativeBindGenerator qt \
  --header my_object.hpp \
  --library-name myqtbridge \
  -- \
  -I/path/to/qt/include \
  -std=c++20
```

该模式会对 Qt 类型做专门处理，包括将信号转换为 `connect` 导出、通过 `deleteLater` 释放 `QObject`，以及在适用时转换 `QString` 与 UTF-8 字符串（可用 `--no-use-delete-later` 关闭 `deleteLater`）。请确保 Clang 参数与项目使用的 Qt 版本及构建配置一致。

## 生成器选项

| 选项 | 默认值 | 说明 |
| --- | --- | --- |
| `--header` | 无 | 要解析的头文件；可重复指定，至少需要一个。 |
| `--header-directory` | 无 | 递归搜索头文件的目录；其中的 `.h`/`.hpp` 会自动追加到 `--header`。 |
| `--header-exclude` | 无 | 递归搜索时按相对路径片段排除的目录或文件；可重复指定，例如 `util/windows`。 |
| `--namespace`、`-n` | `OBSharp.Interop.Generated` | 生成的 C# 命名空间。 |
| `--library-name`、`-l` | `native` | `LibraryImport` 使用的原生库名。 |
| `--csharp-output` | `generated/csharp` | C# 文件输出目录。 |
| `--native-output` | `generated/native` | native 产物目录：shim 头文件、实现、PCH 与 CMake 清单都写在这里。 |
| `--native-name` | `cpp_shim` | native 产物基名，同时作为 CMake 目标名；产出 `<基名>.h` / `<基名>.pch.h` / `<基名>.cmake`。 |
| `--export-prefix` | `cpp_shim` | C 侧句柄与导出符号的前缀。 |
| `--max-exports-per-library` | `65535` | 每个共享库的最大导出符号数，即 PE 导出表上限；超出后按库序号拆成多个库。 |
| `--max-exports-per-source` | `4096` | 每个实现文件的最大导出数，用于把单个 `.cpp` 控制在不影响编译耗时的规模。 |
| `--methods-per-file` | `128` | 每个托管方法文件最多包含的方法或属性数量。 |
| `--write-marshaller` | 开启 | 是否写出转换头文件；同一项目分批生成时置为关闭以避免重复。 |
| `--four-byte-boolean` | 关闭 | 将 `bool` 按 4 字节 `UnmanagedType.Bool` 封送；默认按 1 字节 `UnmanagedType.I1`。 |
| `--string-marshaller` | 默认 UTF-8 封送 | 为 `const char*` 参数指定自定义 marshaller 类型名。 |
| `--return-string-marshaller` | 默认 UTF-8 封送 | 为 `const char*` 返回值指定自定义 marshaller 类型名。 |
| `--nullable-handle-marshaller` | 默认关闭 | 为句柄参数指定可空 marshaller 类型名；指定后包装 API 的句柄参数生成 `Xxx?`，托管 `null` 映射为原生 NULL。 |
| `--relative-includes` | 关闭 | 把 `<native-name>.pch.h` 里的 `#include` 改写为相对 `--native-output` 的路径，便于 ccache/sccache 跨机器复用。默认关闭时写绝对路径。 |
| `--quiet` | 关闭 | 不输出中文进度信息（跳过提示与生成统计），只保留错误信息。CMake 3.20 的 `execute_process` 只能按 ANSI 捕获输出，中文进度在其日志中会乱码，此时可开启本开关。 |

`qt` 子命令另有三个选项：`--connection-type-name`（默认 `qt_shim_connection`）、`--disconnect-function-name`（默认 `qt_shim_disconnect`）、`--use-delete-later`（默认开启，可用 `--no-use-delete-later` 关闭）。

自定义 marshaller 参数只需填写类型名，例如 `MyStringMarshaller`；生成代码会使用 `MarshalUsing(typeof(MyStringMarshaller))`。自定义类型必须能被生成后的 C# 代码访问，并符合 .NET interop marshalling 的要求。

`--nullable-handle-marshaller` 遵循同样的填写方式。指定后：包装 API 的句柄参数是 `Xxx?`，导入声明是 `[MarshalUsing(typeof(该类型))] SafeHandle?`，同一个 marshaller 覆盖所有生成的句柄类型；marshaller 需按 `[CustomMarshaller(typeof(SafeHandle), MarshalMode.ManagedToUnmanagedIn, typeof(...))]` 声明。句柄**返回值**不受该开关影响，始终映射为普通 `Xxx`，由 .NET 内置的 `SafeHandleMarshaller<T>.ManagedToUnmanagedOut` 处理。

### 变参函数

自 alpha.7 起移除了 `--allow-variadic` 开关，**变参函数默认只绑定 `...` 之前的固定参数**：

- C# 方法只包含 `...` 之前的参数，不生成 `params` 数组，调用时无法再传入变参实参；例如 `void blog(int log_level, const char *format, ...);` 生成 `blog(int log_level, [MarshalAs(UnmanagedType.LPUTF8Str)] string? format)`。
- XML 文档注释中的原生声明保留 `...`，例如 `<c>void blog(int, const char *, ...)</c>`，并附一条说明。
- 变参函数指针（如结构体字段 `int (*handler)(const char*, ...)`）仍会被**跳过**，并在 stderr 打印跳过提示。

> 注意：只绑定定参部分并不能解决变参调用本身的 ABI 问题。在 x86-64 SysV（Linux/macOS）上，调用变参函数时调用方需通过 `AL` 寄存器告知使用的向量寄存器个数；`LibraryImport` 生成的调用不会设置该寄存器，若原生函数经由 `va_arg` 读取浮点变参，行为是未定义的。只有当原生函数不读取浮点变参，或平台/调用约定不依赖 `AL`（例如 Windows x64）时，这样生成的绑定才可信赖。

## 类型映射概览

### C 声明

- 常见 C 整数和浮点类型映射到对应的 C# 基本类型；`long` 和 `unsigned long` 分别映射为 `CLong` 和 `CULong`。
- `size_t` 和 `uintptr_t` 映射为随进程位数变化的 `nuint`；`ptrdiff_t`、`ssize_t` 和 `intptr_t` 映射为 `nint`。定宽整数按原生符号性映射为对应的 C# 整数类型；普通 `char` 映射为 `byte`，`signed char` 映射为 `sbyte`。
- `const char*` 参数和返回值默认按 UTF-8 字符串处理；可分别通过 `--string-marshaller` 和 `--return-string-marshaller` 指定自定义 marshaller。
- `bool` 参数和返回值默认使用 1 字节封送；`--four-byte-boolean` 可切换为 4 字节封送。
- 函数指针生成为 `delegate* unmanaged`；数组以指针形式表示。
- 完整定义的结构体、联合体和枚举会生成对应 C# 类型；结构体中的定长数组使用 `InlineArray`。
- 对象及不透明结构体指针可生成为 `SafeHandle`。符合常见释放函数形式的声明（例如接收单个不透明指针、返回 `void` 且函数名包含 `destroy`、`free`、`release`、`close` 或 `delete`）可用于关联句柄释放操作。句柄参数默认生成 `Xxx`；指定 `--nullable-handle-marshaller` 后，包装 API 生成 `Xxx?`，允许把托管 `null` 传给原生 NULL，导入声明一侧仍是 `SafeHandle?`。

#### 句柄类型的构造与工厂

- 每个句柄类都有一个**公开无参构造函数**，它只构造**无效句柄**（`IsInvalid == true`），不会调用任何原生函数。这是因为 .NET 在把原生返回值封送成 `SafeHandle` 时会先调用公开无参构造函数、再注入原生指针；若该构造函数里创建原生对象，每次返回值封送都会先多创建一个对象再被覆盖，造成泄漏。
- 无参 `create` 函数（如 `obs_data_create`）生成**静态工厂**：`public static Xxx Create() => new((nint)xxx_create(), true);`，由它接管新句柄的所有权。
- 带参数的 `create` 函数生成托管**构造函数重载**，例如 `public Xxx(int a) : this((nint)xxx_create(a), true)`。
- 因此 `new Xxx()` 与 `Xxx.Create()` 语义不同：前者只得到一个无效句柄，后者才真正创建原生对象。
- 句柄类型的业务成员若与 `SafeHandle` 的公开成员（`IsInvalid`、`Handle`、`Close`、`Dispose` 等）重名，托管侧成员名会**追加一个下划线**（例如 `IsInvalid` → `IsInvalid_`），并在 XML 文档注释里说明原因；C 侧导出符号名不变。

### C++ 转发

`cpp` 子命令会为头文件中的可处理 C++ 类和枚举生成 C 式导出。该模式支持常见标量、枚举、类对象和指针，以及部分字符串和 `std::vector` 转换。指向未在指定头文件中发现定义的 C++ 类的指针会回退为 `nint`，不会仅因指针类型无法细化映射而跳过。模板特化指针使用 C# 泛型不透明结构体（例如 `QList<nint>*`），避免把 C++ 模板拼写直接当作 C# 标识符；跨模块的不透明指针不会重复声明冲突的托管类型。具体可处理的类型取决于声明形式以及传入的 Clang 参数。

`qt` 子命令的自动数组转换支持元素为 C 基本类型或指针的 `QList<T>`；托管 API 使用 `ReadOnlySpan<T>`，指针元素以 `nint` 表示，native 边界使用元素指针与 `size_t`，并在 C++ 包装中转换为 `QList<T>`。返回值会复制到托管内存后以只读跨度返回。其他复杂值类型的 `QList<T>` 尚未支持自动跨度转换。`size_t`、`uintptr_t` 等无符号原生宽度整数映射为 `nuint`；`ptrdiff_t` 等有符号原生宽度整数映射为 `nint`，宽度均随目标平台变化。

## 注意事项

- 生成器只分析指定头文件中的声明；包含目录、宏定义、平台宏和语言标准等配置应通过 `--` 传给 Clang。
- `--` 之后的参数会原样传递，不会由生成器自动补充。请为 C 头文件和 C++ 头文件分别提供合适的语言标准和编译配置。
- C# 绑定仅描述互操作声明，不会构建、复制或部署目标原生库。
- 生成器会清理 C# 输出目录中的旧 `*.g.cs` 文件后再写入新结果；请将该目录专用于生成文件。
- 遇到无法映射的类型或 Clang 解析错误时，请先检查头文件及其编译参数；部分不支持的类型可能会被跳过。

## 许可证

本项目使用 MIT 许可证，详见 [`LICENSE`](./LICENSE)。