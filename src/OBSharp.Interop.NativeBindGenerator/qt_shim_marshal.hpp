// 由 OBSharp.Interop.NativeBindGenerator 生成：Qt 类型与 C 导出类型之间的转换工具。
// 只负责类型转换，不负责内存分配与释放：缓冲区由调用方提供，或按借用语义交给托管侧复制。
#pragma once

#include "cpp_shim_marshal.hpp"

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

// 说明：本头文件假定调用方已包含 Qt 的 <QString>/<QByteArray> 等声明；
// 它们通常由 shim 的预编译头（原始头文件集合）提供，这里不主动包含 Qt 头，避免依赖 Qt 的安装路径。

// ── 导出与调用约定（沿用通用 shim 的宏） ──────────────────────────
// CPP_SHIM_EXPORT / CPP_SHIM_CALL 由 cpp_shim_marshal.hpp 提供。

namespace QtShimMarshal
{
    // ── QString <-> const char*（UTF-8） ───────────────────────────
    // 借用语义：返回的指针指向传入字符串的内部缓冲区，生命周期由该 QString 决定；
    // 传入临时对象会立即悬垂，调用方需保证 QString 在本表达式期间存活。

    // QString → C 侧 const char*（借用 QString 的内部 UTF-8 视图）。
    // QString 不保证 UTF-8 连续存储，因此需要由调用方提供稳定的 QByteArray 中转，
    // 这里只负责把已有的 QByteArray 借出。
    inline const char* ToC(const char* utf8, std::size_t* size) noexcept
    {
        if (size != nullptr)
            *size = utf8 == nullptr ? 0 : std::char_traits<char>::length(utf8);

        return utf8;
    }

    // C 侧 const char* → 目标字符串类型。
    // 显式返回类型由模板参数决定，避免对 Qt 头产生本文件级别的依赖。
    template <typename TString>
    inline TString ToCpp(const char* text)
    {
        return text == nullptr ? TString() : TString::fromUtf8(text);
    }

    // 借用视图：把 C 侧的 (指针, 长度) 转成 string_view，不做拷贝。
    inline std::string_view View(const char* text, std::size_t size) noexcept
    {
        return text == nullptr ? std::string_view() : std::string_view(text, size);
    }

    // ── QList/QVector <-> (指针, 长度) ────────────────────────────
    // 借用语义：直接暴露容器的连续存储，不做拷贝；
    // 与 std::vector 一样，std::vector<bool> 之类的位压缩特化不适用。

    template <typename TContainer>
    inline const typename TContainer::value_type* ToC(const TContainer& container, std::size_t* size) noexcept
    {
        if (size != nullptr)
            *size = static_cast<std::size_t>(container.size());

        return container.size() == 0 ? nullptr : container.constData();
    }

    template <typename TContainer>
    inline TContainer ToCpp(const typename TContainer::value_type* data, std::size_t count)
    {
        if (data == nullptr || count == 0)
            return TContainer();

        TContainer result;
        result.reserve(static_cast<int>(count));
        for (std::size_t i = 0; i < count; ++i)
            result.append(data[i]);
        return result;
    }
}
