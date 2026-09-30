#include <crash_maps.h>
#include <hooking/il2cpp.h>
#include <logger.h>
#include <dlfcn.h>
#include <algorithm>
#include <charconv>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string_view>
#include <utility>
#include <vector>

#define TAG "FusionCrashMaps"

namespace fs = std::filesystem;

namespace
{
    const char *const METHOD_MAP_NAME = "il2cpp-methods.map";
    const char *const METHOD_MAP_HEADER = "# il2cpp-map v1 size=";

    using domain_get_t = void *(*)();
    using domain_get_assemblies_t = void **(*)(void *domain, size_t *count);
    using assembly_get_image_t = void *(*)(void *assembly);
    using image_get_class_count_t = size_t (*)(void *image);
    using image_get_class_t = void *(*)(void *image, size_t index);
    using class_get_methods_t = void *(*)(void *klass, void **iter);
    using class_get_string_t = const char *(*)(void *klass);
    using class_get_declaring_type_t = void *(*)(void *klass);

    struct Il2CppApi
    {
        domain_get_t domain_get;
        domain_get_assemblies_t domain_get_assemblies;
        assembly_get_image_t assembly_get_image;
        image_get_class_count_t image_get_class_count;
        image_get_class_t image_get_class;
        class_get_methods_t class_get_methods;
        class_get_string_t class_get_name;
        class_get_string_t class_get_namespace;
        class_get_declaring_type_t class_get_declaring_type;
    };

    template<typename T>
    bool resolve(void *handle, const char *name, T &out)
    {
        out = reinterpret_cast<T>(dlsym(handle, name));
        if (!out)
        {
            log_format(LogLevel::WARN, TAG, "{} is not exported by libil2cpp", name);
            return false;
        }
        return true;
    }

    bool resolve_api(Il2CppApi &api)
    {
        void *handle = il2cpp_get_handle();
        if (!handle)
        {
            return false;
        }

        // Every symbol is attempted so the log names all that are missing.
        bool ok = true;
        ok &= resolve(handle, "il2cpp_domain_get", api.domain_get);
        ok &= resolve(handle, "il2cpp_domain_get_assemblies", api.domain_get_assemblies);
        ok &= resolve(handle, "il2cpp_assembly_get_image", api.assembly_get_image);
        ok &= resolve(handle, "il2cpp_image_get_class_count", api.image_get_class_count);
        ok &= resolve(handle, "il2cpp_image_get_class", api.image_get_class);
        ok &= resolve(handle, "il2cpp_class_get_methods", api.class_get_methods);
        ok &= resolve(handle, "il2cpp_class_get_name", api.class_get_name);
        ok &= resolve(handle, "il2cpp_class_get_namespace", api.class_get_namespace);
        ok &= resolve(handle, "il2cpp_class_get_declaring_type", api.class_get_declaring_type);
        return ok;
    }

    // "Namespace.Outer/Inner". A nested type has no namespace of its own, so the outermost
    // type supplies it.
    std::string class_full_name(const Il2CppApi &api, void *klass)
    {
        std::string name = api.class_get_name(klass);
        void *outermost = klass;
        for (void *outer = api.class_get_declaring_type(klass); outer; outer = api.class_get_declaring_type(outer))
        {
            name.insert(0, "/");
            name.insert(0, api.class_get_name(outer));
            outermost = outer;
        }

        const char *ns = api.class_get_namespace(outermost);
        if (ns && *ns)
        {
            name.insert(0, ".");
            name.insert(0, ns);
        }
        return name;
    }

    // True when the map already in place was written for a libil2cpp of this size.
    bool is_current(const fs::path &map_path, uintmax_t library_file_size)
    {
        std::ifstream in(map_path);
        std::string header;
        if (!in || !std::getline(in, header))
        {
            return false;
        }

        const std::string_view prefix(METHOD_MAP_HEADER);
        if (header.compare(0, prefix.size(), prefix) != 0)
        {
            return false;
        }

        uintmax_t recorded = 0;
        const char *begin = header.data() + prefix.size();
        const char *end = header.data() + header.size();
        auto parsed = std::from_chars(begin, end, recorded);
        return parsed.ec == std::errc() && recorded == library_file_size;
    }

    void prune(const fs::path &directory, std::string_view prefix, size_t keep)
    {
        std::error_code ec;
        std::vector<std::pair<fs::file_time_type, fs::path>> maps;
        for (const auto &entry : fs::directory_iterator(directory, ec))
        {
            const std::string name = entry.path().filename().string();
            if (name.size() <= prefix.size() || name.compare(0, prefix.size(), prefix) != 0 ||
                entry.path().extension() != ".map")
            {
                continue;
            }

            std::error_code time_ec;
            auto written = entry.last_write_time(time_ec);
            if (!time_ec)
            {
                maps.emplace_back(written, entry.path());
            }
        }

        if (maps.size() <= keep)
        {
            return;
        }

        std::sort(maps.begin(), maps.end(), [](const auto &a, const auto &b) { return a.first > b.first; });
        for (size_t i = keep; i < maps.size(); i++)
        {
            std::error_code remove_ec;
            fs::remove(maps[i].second, remove_ec);
        }
        log_format(LogLevel::INFO, TAG, "Removed {} old {}*.map", maps.size() - keep, prefix);
    }
}

void crash_maps_prune_perf_maps(const std::string &directory, size_t keep)
{
    // "perf-" does not match "perfinfo-", so the two kinds are counted separately.
    prune(directory, "perf-", keep);
    prune(directory, "perfinfo-", keep);
}

void crash_maps_write_il2cpp_methods(const std::string &directory,
                                     const std::string &original_library_path,
                                     uintptr_t library_base,
                                     size_t library_size)
{
    std::error_code ec;
    const uintmax_t library_file_size = fs::file_size(original_library_path, ec);
    if (ec || library_base == 0 || library_size == 0)
    {
        log_format(LogLevel::WARN, TAG, "Skipping the il2cpp method map: cannot identify {}", original_library_path);
        return;
    }

    const fs::path map_path = fs::path(directory) / METHOD_MAP_NAME;
    if (is_current(map_path, library_file_size))
    {
        log(LogLevel::INFO, TAG, "il2cpp method map is up to date");
        return;
    }

    Il2CppApi api{};
    if (!resolve_api(api))
    {
        return;
    }

    const auto started = std::chrono::steady_clock::now();

    std::vector<std::pair<uint32_t, std::string>> methods;
    methods.reserve(1 << 17);

    size_t assembly_count = 0;
    void **assemblies = api.domain_get_assemblies(api.domain_get(), &assembly_count);
    for (size_t a = 0; a < assembly_count; a++)
    {
        void *image = api.assembly_get_image(assemblies[a]);
        if (!image)
        {
            continue;
        }

        const size_t class_count = api.image_get_class_count(image);
        for (size_t c = 0; c < class_count; c++)
        {
            void *klass = api.image_get_class(image, c);
            if (!klass)
            {
                continue;
            }

            std::string class_name;
            void *iter = nullptr;
            while (void *method = api.class_get_methods(klass, &iter))
            {
                // MethodInfo starts with the pointer to the compiled method body.
                const uintptr_t address = *static_cast<const uintptr_t *>(method);
                if (address < library_base || address - library_base >= library_size)
                {
                    continue;
                }

                if (class_name.empty())
                {
                    class_name = class_full_name(api, klass);
                }
                methods.emplace_back(static_cast<uint32_t>(address - library_base),
                                     class_name + "::" + il2cpp_method_get_name(method));
            }
        }
    }

    const auto enumerated = std::chrono::steady_clock::now();

    std::stable_sort(methods.begin(), methods.end(),
                     [](const auto &a, const auto &b) { return a.first < b.first; });

    // Written under a temporary name: a partial file never carries a header that looks current.
    const fs::path temp_path = fs::path(directory) / (std::string(METHOD_MAP_NAME) + ".tmp");
    FILE *out = fopen(temp_path.c_str(), "w");
    if (!out)
    {
        log_format(LogLevel::WARN, TAG, "Cannot write {}", temp_path.string());
        return;
    }

    std::vector<char> buffer(1 << 20);
    setvbuf(out, buffer.data(), _IOFBF, buffer.size());
    // `image` is where the loaded library ends, as an offset like the entries below: an address
    // past the last entry still belongs to that method only while it is below this bound.
    fprintf(out, "%s%ju methods=%zu image=%zx\n", METHOD_MAP_HEADER, library_file_size, methods.size(),
            library_size);
    for (const auto &[rva, name] : methods)
    {
        fprintf(out, "%x %s\n", rva, name.c_str());
    }
    const bool failed = ferror(out) != 0;
    if (fclose(out) != 0 || failed)
    {
        log_format(LogLevel::WARN, TAG, "Failed writing {}", temp_path.string());
        fs::remove(temp_path, ec);
        return;
    }

    fs::rename(temp_path, map_path, ec);
    if (ec)
    {
        log_format(LogLevel::WARN, TAG, "Cannot move {} into place: {}", temp_path.string(), ec.message());
        fs::remove(temp_path, ec);
        return;
    }

    const auto finished = std::chrono::steady_clock::now();
    using std::chrono::duration_cast;
    using std::chrono::milliseconds;
    log_format(LogLevel::INFO, TAG, "Wrote il2cpp method map: {} methods, {} ms (enumeration {} ms)",
               methods.size(),
               duration_cast<milliseconds>(finished - started).count(),
               duration_cast<milliseconds>(enumerated - started).count());
}
