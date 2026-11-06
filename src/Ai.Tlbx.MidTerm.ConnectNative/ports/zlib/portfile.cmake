# When this port is updated, the minizip port should be updated at the same time
vcpkg_from_github(
    OUT_SOURCE_PATH SOURCE_PATH
    REPO madler/zlib
    REF v${VERSION}
    SHA512 16fea4df307a68cf0035858abe2fd550250618a97590e202037acd18a666f57afc10f8836cbbd472d54a0e76539d0e558cb26f059d53de52ff90634bbf4f47d4
    HEAD_REF master
    PATCHES
        0001-no-version-script-on-ohos.patch
        CVE-2026-85091.patch
)

# Exact upstream CVE-2026-85091 backport; fail before compiling any different source.
file(SHA256 "${CMAKE_CURRENT_LIST_DIR}/CVE-2026-85091.patch" TLBX_PATCH_SHA256)
file(SHA256 "${SOURCE_PATH}/gzwrite.c" TLBX_SOURCE_SHA256)
if(NOT TLBX_PATCH_SHA256 STREQUAL "110ff14375733173d8aa54574473424fbd7dfe4b81f1ca34a759c6fe14b15b14" OR
   NOT TLBX_SOURCE_SHA256 STREQUAL "31d0da140edf382b82b13e2d87a22f59e158d9014ee026459e01cf437d0ab07d")
    message(FATAL_ERROR "The pinned zlib upstream backport/source does not match its reviewed fix")
endif()

string(COMPARE EQUAL "${VCPKG_LIBRARY_LINKAGE}" "dynamic" ZLIB_BUILD_SHARED)
string(COMPARE EQUAL "${VCPKG_LIBRARY_LINKAGE}" "static" ZLIB_BUILD_STATIC)

vcpkg_cmake_configure(
    SOURCE_PATH "${SOURCE_PATH}"
    OPTIONS
        -DZLIB_BUILD_TESTING=OFF
        -DZLIB_BUILD_SHARED=${ZLIB_BUILD_SHARED}
        -DZLIB_BUILD_STATIC=${ZLIB_BUILD_STATIC}
)

vcpkg_cmake_install()
if(VCPKG_TARGET_IS_WINDOWS)
    set(TLBX_NATIVE_ARCHIVE "zs.lib")
else()
    set(TLBX_NATIVE_ARCHIVE "libz.a")
endif()
file(SHA256 "${CURRENT_PACKAGES_DIR}/lib/${TLBX_NATIVE_ARCHIVE}" TLBX_ARCHIVE_SHA256)
file(WRITE "${CURRENT_PACKAGES_DIR}/share/zlib/tlbx-backport.json"
    "{\"version\":1,\"cve\":\"CVE-2026-85091\",\"commit\":\"df84af25dc1942490e1d1c899a07619152a46148\",\"patchSha256\":\"${TLBX_PATCH_SHA256}\",\"sourceSha256\":\"${TLBX_SOURCE_SHA256}\",\"archive\":\"${TLBX_NATIVE_ARCHIVE}\",\"archiveSha256\":\"${TLBX_ARCHIVE_SHA256}\"}\n")
vcpkg_copy_pdbs()
vcpkg_cmake_config_fixup(CONFIG_PATH "lib/cmake/zlib")
vcpkg_fixup_pkgconfig()

if(VCPKG_TARGET_IS_WINDOWS AND VCPKG_LIBRARY_LINKAGE STREQUAL "static")
    vcpkg_replace_string("${CURRENT_PACKAGES_DIR}/lib/pkgconfig/zlib.pc" " -lz" " -lzs")
endif()
if(NOT VCPKG_BUILD_TYPE)
    vcpkg_replace_string("${CURRENT_PACKAGES_DIR}/debug/lib/pkgconfig/zlib.pc" "/include" "/../include")
    if(VCPKG_TARGET_IS_WINDOWS)
        if(VCPKG_LIBRARY_LINKAGE STREQUAL "static")
            vcpkg_replace_string("${CURRENT_PACKAGES_DIR}/debug/lib/pkgconfig/zlib.pc" " -lz" " -lzsd")
        else()
            vcpkg_replace_string("${CURRENT_PACKAGES_DIR}/debug/lib/pkgconfig/zlib.pc" " -lz" " -lzd")
        endif()
    endif()
endif()

if(VCPKG_LIBRARY_LINKAGE STREQUAL "static")
    vcpkg_replace_string("${CURRENT_PACKAGES_DIR}/include/zconf.h" "ifdef ZLIB_DLL" "if 0")
    vcpkg_replace_string("${CURRENT_PACKAGES_DIR}/share/zlib/ZLIBConfig.cmake" [[_ZLIB_supported_components "shared" "static"]] [[_ZLIB_supported_components "static"]])
else()
    vcpkg_replace_string("${CURRENT_PACKAGES_DIR}/include/zconf.h" "ifdef ZLIB_DLL" "if 1")
    vcpkg_replace_string("${CURRENT_PACKAGES_DIR}/share/zlib/ZLIBConfig.cmake" [[_ZLIB_supported_components "shared" "static"]] [[_ZLIB_supported_components "shared"]])
endif()

file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/debug/include")
file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/debug/share")

file(INSTALL "${CMAKE_CURRENT_LIST_DIR}/vcpkg-cmake-wrapper.cmake" DESTINATION "${CURRENT_PACKAGES_DIR}/share/${PORT}")
file(COPY "${CMAKE_CURRENT_LIST_DIR}/usage" DESTINATION "${CURRENT_PACKAGES_DIR}/share/${PORT}")
vcpkg_install_copyright(FILE_LIST "${SOURCE_PATH}/LICENSE")
