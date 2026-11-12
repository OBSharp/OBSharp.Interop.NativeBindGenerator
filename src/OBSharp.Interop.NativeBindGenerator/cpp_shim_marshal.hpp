// 由 OBSharp.Interop.NativeBindGenerator 生成：C 导出层使用的导出与调用约定宏。
// 只负责导出属性与调用约定；extern "C" 由 shim 头文件的链接块提供。
#pragma once

// ── 导出与调用约定 ──────────────────────────────────────────────────
#if defined(_WIN32)

#ifndef CPP_SHIM_EXPORT
#define CPP_SHIM_EXPORT __declspec(dllexport)
#endif // CPP_SHIM_EXPORT

#ifndef CPP_SHIM_CALL
#define CPP_SHIM_CALL __cdecl
#endif // CPP_SHIM_CALL

#else // !defined(_WIN32)

#ifndef CPP_SHIM_EXPORT
#define CPP_SHIM_EXPORT __attribute__((visibility("default")))
#endif // CPP_SHIM_EXPORT

#ifndef CPP_SHIM_CALL
#define CPP_SHIM_CALL
#endif // CPP_SHIM_CALL

#endif // _WIN32

// ── Marshaller ─────────────────────────────────────────────────────
#include <cstddef>
#include <string>
#include <type_traits>
#include <utility>
#include <vector>

namespace CppShimMarshall
{
    // ── std::basic_string ──────────────────────────────────────────

    // std::basic_string<CharT> → C 风格字符串。
    // 生命周期：返回指针与 str 绑定，str 失效后指针失效；禁止传入临时对象。
    template <typename CharT, typename Traits = std::char_traits<CharT>,
        typename Alloc = std::allocator<CharT>>
        inline const CharT* ToC(const std::basic_string<CharT, Traits, Alloc>& str) noexcept
    {
        return str.c_str();
    }

    // C 风格字符串 → std::basic_string<CharT>。
    // nullptr 返回空串，避免 std::basic_string(nullptr) 的 UB。
    template <typename CharT, typename Traits = std::char_traits<CharT>,
        typename Alloc = std::allocator<CharT>>
        inline std::basic_string<CharT, Traits, Alloc> ToCpp(const CharT* cStr)
    {
        return cStr ? std::basic_string<CharT, Traits, Alloc>(cStr)
            : std::basic_string<CharT, Traits, Alloc>{};
    }

    // ── std::vector ────────────────────────────────────────────────

    // std::vector<T> → (data, size) 对。
    // 生命周期：返回指针与 vec 绑定；禁止传入临时对象。
    // 拒绝 vector<bool>：其为位压缩特化，没有连续的 data()。
    template <typename T, typename Alloc = std::allocator<T>>
    inline std::pair<const T*, std::size_t> ToC(const std::vector<T, Alloc>& vec) noexcept
    {
        static_assert(!std::is_same<T, bool>::value,
            "std::vector<bool> 为位压缩特化，无连续存储；请改用 std::vector<std::uint8_t> 或 std::vector<char>");
        return { vec.data(), vec.size() };
    }

    // (data, count) 对 → std::vector<T>。
    // nullptr 或 count == 0 返回空 vector，避免指针算术与构造的 UB。
    template <typename T, typename Alloc = std::allocator<T>>
    inline std::vector<T, Alloc> ToCpp(const T* data, std::size_t count)
    {
        if (data == nullptr || count == 0)
            return std::vector<T, Alloc>{};
        return std::vector<T, Alloc>(data, data + count);
    }
}
