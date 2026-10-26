namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 随生成物一并写出的 native 侧转换工具（cpp_marshaller.hpp）。
/// </summary>
internal static class CppMarshaller
{
    public const string FileName = "cpp_marshaller.hpp";

    public const string Source = """
        // 由 OBSharp.Interop.NativeBindGenerator 生成：C++ 类型与 C 基本类型之间的转换工具。
        #pragma once

        #include <cstddef>
        #include <cstdint>
        #include <cstdlib>
        #include <cstring>
        #include <memory>
        #include <new>
        #include <string>
        #include <string_view>
        #include <type_traits>
        #include <utility>
        #include <vector>

        #if defined(_WIN32)
        #define CPPM_EXPORT extern "C" __declspec(dllexport)
        #ifndef CPPM_CALL
        #define CPPM_CALL __cdecl
        #endif
        // 与 .NET 默认的 UTF-8 / 数组 marshaller 使用同一分配器（CoTaskMem），托管侧才能正确释放
        extern "C" __declspec(dllimport) void* __stdcall CoTaskMemAlloc(size_t size);
        extern "C" __declspec(dllimport) void __stdcall CoTaskMemFree(void* pointer);
        #pragma comment(lib, "ole32.lib")
        #else
        #define CPPM_EXPORT extern "C" __attribute__((visibility("default")))
        #ifndef CPPM_CALL
        #define CPPM_CALL
        #endif
        #endif

        namespace cppm
        {
            inline void* allocate(size_t size)
            {
        #if defined(_WIN32)
                return CoTaskMemAlloc(size);
        #else
                return std::malloc(size);
        #endif
            }

            inline void release(void* pointer)
            {
        #if defined(_WIN32)
                CoTaskMemFree(pointer);
        #else
                std::free(pointer);
        #endif
            }

            // ---- 字符串 ----

            inline std::string from_c_string(const char* text)
            {
                return text ? std::string(text) : std::string();
            }

            inline std::string_view from_c_view(const char* text)
            {
                return text ? std::string_view(text) : std::string_view();
            }

            inline char* dup_string(std::string_view text)
            {
                char* copy = static_cast<char*>(allocate(text.size() + 1));
                if (!copy)
                    return nullptr;
                if (!text.empty())
                    std::memcpy(copy, text.data(), text.size());
                copy[text.size()] = '\0';
                return copy;
            }

            inline char* dup_string(const char* text)
            {
                return text ? dup_string(std::string_view(text)) : nullptr;
            }

            // ---- 数组 ----

            template <class T>
            inline std::vector<T> to_vector(const T* items, size_t count)
            {
                return (items && count) ? std::vector<T>(items, items + count) : std::vector<T>();
            }

            // 返回由 allocate 分配的拷贝，须通过 cppm_free_memory 释放
            template <class V>
            inline auto to_c_array(const V& values, size_t* count)
                -> std::remove_cv_t<typename V::value_type>*
            {
                using T = std::remove_cv_t<typename V::value_type>;
                const size_t size = values.size();
                if (count)
                    *count = size;
                if (size == 0)
                    return nullptr;
                T* copy = static_cast<T*>(allocate(size * sizeof(T)));
                if (copy)
                    std::memcpy(copy, values.data(), size * sizeof(T));
                return copy;
            }

            // ---- 类对象 ----

            template <class T>
            inline void* make_default()
            {
                if constexpr (std::is_default_constructible_v<T>)
                    return new T();
                else
                    return nullptr;
            }

            // 按值返回的对象：在堆上构造拷贝，所有权交给调用方（须调用 *_destroy）
            template <class T>
            inline void* to_owned(T&& value)
            {
                return new std::remove_cv_t<std::remove_reference_t<T>>(std::forward<T>(value));
            }

            // 返回指针/引用：对象归原持有者所有，仅借用
            template <class T>
            inline void* ptr_to_void(T* pointer)
            {
                return const_cast<std::remove_cv_t<T>*>(pointer);
            }

            template <class T>
            inline void* ref_to_void(T& reference)
            {
                return const_cast<std::remove_cv_t<T>*>(std::addressof(reference));
            }
        }
        """;
}
