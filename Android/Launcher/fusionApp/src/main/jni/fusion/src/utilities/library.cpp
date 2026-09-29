// Copyright (c) 2025 XtraCube

#include <utilities/library.h>
#include <bits/sysconf.h>
#include <algorithm>
#include <dlfcn.h>
#include <fcntl.h>
#include <fstream>
#include <iterator>
#include <string>
#include <sys/stat.h>
#include <unistd.h>
#include <logger.h>

#define TAG "LibraryUtils"

uintptr_t get_module_base(const char* lib_name, const char* known_export_symbol) {
    void* handle = dlopen(lib_name, RTLD_NOLOAD | RTLD_LAZY);
    if (!handle) {
        handle = dlopen(lib_name, RTLD_NOW);
    }
    if (!handle) return 0;

    void* symbol_addr = dlsym(handle, known_export_symbol);
    dlclose(handle);

    if (!symbol_addr) return 0;

    Dl_info info;
    if (dladdr(symbol_addr, &info) && info.dli_fbase) {
        return reinterpret_cast<uintptr_t>(info.dli_fbase);
    }

    return 0;
}

PaddedOpenResult padded_dlopen(const char *library_name,
                               const char *temp_path,
                               size_t pool_size)
{
    // The padded copy is hashed by BepInEx (interop cache key), so its bytes must not depend on
    // the device: the segment end and the pool are rounded to a fixed 16 KiB, which is a page
    // multiple on both 4 KiB and 16 KiB kernels.
    auto page_size = std::max<long>(sysconf(_SC_PAGESIZE), 16 * 1024);

    auto align_up = [](Elf_Addr addr, Elf_Xword align)
    {
        return (addr + align - 1) & ~(align - 1);
    };

    std::ifstream file(library_name, std::ios::binary);
    if (!file)
    {
        log_format(LogLevel::ERROR, TAG, "Failed to open file: {}", library_name);
        return {nullptr, nullptr, 0, 0};
    }

    // Read ELF header
    Elf_Ehdr elf_header{};
    file.read(reinterpret_cast<char *>(&elf_header), sizeof(elf_header));
    if (file.gcount() != sizeof(elf_header) || elf_header.e_type != ET_DYN)
    {
        log_format(LogLevel::ERROR, TAG, "Invalid ELF file: {}", library_name);
        return {nullptr, nullptr, 0, 0};
    }

    // Read program headers
    long long phoff = static_cast<long long>(elf_header.e_phoff);
    file.seekg(phoff, std::ios::beg);
    auto phdrs = new Elf_Phdr[elf_header.e_phnum];
    file.read(reinterpret_cast<char *>(phdrs), elf_header.e_phnum * sizeof(Elf_Phdr));
    if (file.gcount() != static_cast<std::streamsize>(elf_header.e_phnum * sizeof(Elf_Phdr)))
    {
        log_format(LogLevel::ERROR, TAG, "Failed to read PHDRs from file: {}", library_name);
        delete[] phdrs;
        return {nullptr, nullptr, 0, 0};
    }

    // Find the last program segment and the base vaddr
    Elf_Phdr *last_phdr = nullptr;
    Elf_Addr base_vaddr = ~static_cast<Elf_Addr>(0);

    for (int i = 0; i < elf_header.e_phnum; ++i)
    {
        const auto &ph = phdrs[i];
        if (ph.p_type == PT_LOAD)
        {
            base_vaddr = std::min(base_vaddr, ph.p_vaddr);
            if (!last_phdr || (ph.p_vaddr + ph.p_memsz > last_phdr->p_vaddr + last_phdr->p_memsz))
            {
                last_phdr = &phdrs[i];
            }
        }
    }

    // Calculate the absolute offset for the trampoline pool
    Elf_Addr segment_end = last_phdr->p_vaddr + last_phdr->p_memsz;
    segment_end = align_up(segment_end, page_size);
    Elf_Addr pool_offset = segment_end - base_vaddr;

    // Pad the last segment
    last_phdr->p_memsz = align_up(last_phdr->p_memsz + pool_size, page_size);

    // Calculate the new pool size
    Elf_Addr new_segment_end = last_phdr->p_vaddr + last_phdr->p_memsz;
    size_t new_pool_size = new_segment_end - segment_end;

    // The padded copy is identical for the same original and pool, so it is reused across
    // launches. A sidecar records what it was built from: the original's path already changes
    // with every game install (the install directory is randomized), and its modification time
    // is a fixed placeholder for APK-extracted libraries, so it is not used.
    // The inode and change time pin the exact installed file: an install that lands on the
    // same path with a same-sized library still gets a fresh copy.
    file.seekg(0, std::ios::end);
    const long long original_size = static_cast<long long>(file.tellg());
    struct stat original_st{};
    if (stat(library_name, &original_st) != 0)
    {
        original_st.st_ino = 0;
        original_st.st_ctime = 0;
    }
    const std::string stamp_path = std::string(temp_path) + ".stamp";
    const std::string stamp = std::string("src=") + library_name + "\nsize=" + std::to_string(original_size)
                            + "\nino=" + std::to_string(static_cast<unsigned long long>(original_st.st_ino))
                            + "\nctime=" + std::to_string(static_cast<long long>(original_st.st_ctime))
                            + "\npool=" + std::to_string(pool_size) + "\npage=" + std::to_string(page_size) + "\n";

    bool reuse = false;
    {
        std::ifstream stamp_file(stamp_path, std::ios::binary);
        std::string recorded((std::istreambuf_iterator<char>(stamp_file)), std::istreambuf_iterator<char>());
        struct stat st{};
        reuse = stamp_file && recorded == stamp && stat(temp_path, &st) == 0 && st.st_size == original_size;
    }

    if (reuse)
    {
        log_format(LogLevel::INFO, TAG, "Reusing padded copy at {}", temp_path);
        delete[] phdrs;
        file.close();
    }
    else
    {
        // The stamp promises a complete copy with patched headers, so it goes away before the
        // rewrite starts and is written back only after the file is closed.
        unlink(stamp_path.c_str());

        // Start writing
        std::ofstream temp_file(temp_path, std::ios::binary | std::ios::trunc);
        if (!temp_file)
        {
            log_format(LogLevel::ERROR, TAG, "Failed to open temp file: {}", temp_path);
            delete[] phdrs;
            return {nullptr, nullptr, 0, 0};
        }

        // Copy original contents
        file.clear();
        file.seekg(0, std::ios::beg);
        auto buffer = new char[page_size];
        while (file.read(buffer, page_size))
        {
            temp_file.write(buffer, page_size);
        }
        if (file.gcount() > 0)
        {
            temp_file.write(buffer, file.gcount());
        }
        delete[] buffer;

        // Write new PHDRs
        temp_file.seekp(elf_header.e_phoff, std::ios::beg);
        temp_file.write(reinterpret_cast<char *>(phdrs), elf_header.e_phnum * sizeof(Elf_Phdr));
        delete[] phdrs;

        temp_file.close();
        file.close();

        if (temp_file.fail())
        {
            log_format(LogLevel::ERROR, TAG, "Failed to write padded copy to {}", temp_path);
            return {nullptr, nullptr, 0, 0};
        }

        // The stamp must never outlive the data it vouches for, so the copy reaches storage
        // before the stamp exists.
        int fd = open(temp_path, O_RDONLY);
        if (fd >= 0)
        {
            fsync(fd);
            close(fd);
        }

        std::ofstream stamp_file(stamp_path, std::ios::binary | std::ios::trunc);
        stamp_file << stamp;
        stamp_file.close();
        if (stamp_file.fail())
        {
            log_format(LogLevel::WARN, TAG, "Could not record the padded copy at {}", stamp_path);
            unlink(stamp_path.c_str());
        }
    }

    // Load the new ELF
    void *handle = dlopen(temp_path, RTLD_GLOBAL | RTLD_NOW);
    if (!handle)
    {
        log_format(LogLevel::ERROR, TAG, "dlopen failed for {}: {}", temp_path, dlerror());
        if (reuse)
        {
            // A reused copy that no longer loads is rebuilt on the next launch.
            unlink(stamp_path.c_str());
        }
        return {nullptr, nullptr, 0, 0};
    }

    static constexpr const char* sym_lookup[] = {"il2cpp_init", "start", "JNI_OnLoad"};
    Dl_info info;

    void *sym_addr = nullptr;
    for (const auto& sym : sym_lookup) {
        sym_addr = dlsym(handle, sym);
        if (sym_addr) {
            break;
        }
    }

    if (!sym_addr) {
        log_format(LogLevel::ERROR, TAG, "Failed to find any known symbol in {}: {}", temp_path, dlerror());
        dlclose(handle);
        return {nullptr, nullptr, 0, 0};
    }

    dladdr(sym_addr, &info);
    if (!info.dli_fbase) {
        log_format(LogLevel::ERROR, TAG, "dladdr failed for symbol in {}: {}", temp_path, dlerror());
        dlclose(handle);
        return {nullptr, nullptr, 0, 0};
    }

    size_t trampoline_base = reinterpret_cast<uintptr_t>(info.dli_fbase) + pool_offset;
    return {handle, info.dli_fbase, trampoline_base, new_pool_size};
}