vcpkg_from_github(
    OUT_SOURCE_PATH SOURCE_PATH
    REPO paullouisageneau/libdatachannel
    REF "v${VERSION}"
    SHA512 95ee4e0be3c524867bf14ecbe438ab3bca08fd939e4ce79ca35b66055ff2cca8e61779eb8ad16ba0f3b2eb86b469f0e82cc0e7bff286f0797607a26c2c2b6e58
    HEAD_REF master
    PATCHES
        dependencies.diff
        uwp-warnings.patch
        ipv6-url.patch
        nice-static-closure.patch
        nice-force-relay.patch
        nice-teardown-lifetime.patch
)

# libnice 0.1.24 legacy NULL receive attachment leaves a callable NULL wrapper.
# True detach, stream removal, and raw-this timer cancellation must all run on
# the IO context before releasing the barrier. Preserve upstream #1103 ordering.
file(SHA256 "${CMAKE_CURRENT_LIST_DIR}/nice-teardown-lifetime.patch" TLBX_TEARDOWN_PATCH_SHA256)
file(SHA256 "${SOURCE_PATH}/src/impl/icetransport.cpp" TLBX_TEARDOWN_SOURCE_SHA256)
if(NOT TLBX_TEARDOWN_PATCH_SHA256 STREQUAL "c23e6a331b8abfcdca8cffe0bea64bcc65a7c61f8700a66625f07e094a9044aa" OR
   NOT TLBX_TEARDOWN_SOURCE_SHA256 STREQUAL "6d9e12854e19a174350ae159969fa144e1d351297573489b84e403719119fd63")
    message(FATAL_ERROR "The reviewed libnice teardown lifetime patch/source does not match")
endif()

vcpkg_check_features(OUT_FEATURE_OPTIONS FEATURE_OPTIONS
    FEATURES
        stdcall CAPI_STDCALL
    INVERTED_FEATURES
        ws      NO_WEBSOCKET
        srtp    NO_MEDIA
)

vcpkg_find_acquire_program(PKGCONFIG)
vcpkg_cmake_configure(
    SOURCE_PATH "${SOURCE_PATH}"
    OPTIONS
        ${FEATURE_OPTIONS}
        -DPREFER_SYSTEM_LIB=ON
        -DUSE_NICE=ON
        "-DPKG_CONFIG_EXECUTABLE=${PKGCONFIG}"
        -DPKG_CONFIG_USE_CMAKE_PREFIX_PATH=ON
        "-DCMAKE_PREFIX_PATH=${CURRENT_INSTALLED_DIR}"
        -DNO_EXAMPLES=ON
        -DNO_TESTS=ON
)

vcpkg_cmake_install()
vcpkg_cmake_config_fixup(CONFIG_PATH lib/cmake/LibDataChannel)

if(VCPKG_LIBRARY_LINKAGE STREQUAL "static")
    vcpkg_replace_string("${CURRENT_PACKAGES_DIR}/include/rtc/common.hpp" "#ifdef RTC_STATIC" "#if 1")
    vcpkg_replace_string("${CURRENT_PACKAGES_DIR}/include/rtc/rtc.h" "#ifdef RTC_STATIC" "#if 1")
endif()

file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/debug/include" "${CURRENT_PACKAGES_DIR}/debug/share")

vcpkg_install_copyright(FILE_LIST "${SOURCE_PATH}/LICENSE")

# vcpkg normally performs these mutations after the portfile returns. Finish
# them before signing and freezing the link output, then suppress the later pass.
if(VCPKG_TARGET_IS_LINUX)
    z_vcpkg_fixup_rpath_in_dir()
    set(VCPKG_FIXUP_ELF_RPATH OFF)
elseif(VCPKG_TARGET_IS_OSX)
    z_vcpkg_fixup_macho_rpath_in_dir()
    set(VCPKG_FIXUP_MACHO_RPATH OFF)
endif()

# Freeze the actual native link/install output against its reviewed static inputs.
if(VCPKG_TARGET_IS_WINDOWS)
    set(TLBX_NATIVE_ENTRY "datachannel.dll")
    set(TLBX_NATIVE_PATH "${CURRENT_PACKAGES_DIR}/bin/${TLBX_NATIVE_ENTRY}")
elseif(VCPKG_TARGET_IS_OSX)
    set(TLBX_NATIVE_ENTRY "libdatachannel.dylib")
    set(TLBX_NATIVE_PATH "${CURRENT_PACKAGES_DIR}/lib/${TLBX_NATIVE_ENTRY}")
    get_filename_component(TLBX_NATIVE_REAL_PATH "${TLBX_NATIVE_PATH}" REALPATH)
    if("$ENV{TLBX_CONNECT_SIGN_IDENTITY}" STREQUAL "")
        message(FATAL_ERROR "macOS native signer identity must be explicit and ABI-tracked")
    endif()
    set(TLBX_SIGN_ARGS --force --sign "$ENV{TLBX_CONNECT_SIGN_IDENTITY}")
    if(NOT "$ENV{TLBX_CONNECT_SIGN_IDENTITY}" STREQUAL "-")
        list(APPEND TLBX_SIGN_ARGS --options runtime --timestamp)
    endif()
    vcpkg_execute_required_process(
        COMMAND /usr/bin/codesign ${TLBX_SIGN_ARGS} "${TLBX_NATIVE_REAL_PATH}"
        WORKING_DIRECTORY "${CURRENT_PACKAGES_DIR}" LOGNAME native-codesign)
    vcpkg_execute_required_process(
        COMMAND /usr/bin/codesign --verify --strict "${TLBX_NATIVE_REAL_PATH}"
        WORKING_DIRECTORY "${CURRENT_PACKAGES_DIR}" LOGNAME native-codesign-verify)
else()
    set(TLBX_NATIVE_ENTRY "libdatachannel.so")
    set(TLBX_NATIVE_PATH "${CURRENT_PACKAGES_DIR}/lib/${TLBX_NATIVE_ENTRY}")
endif()
file(SHA256 "${TLBX_NATIVE_PATH}" TLBX_NATIVE_SHA256)
set(TLBX_BACKPORTS "{}")
if(EXISTS "${CURRENT_INSTALLED_DIR}/share/zlib/tlbx-backport.json")
    file(READ "${CURRENT_INSTALLED_DIR}/share/zlib/tlbx-backport.json" TLBX_ZLIB_PROOF)
    string(JSON TLBX_ZLIB_ARCHIVE GET "${TLBX_ZLIB_PROOF}" archive)
    string(JSON TLBX_ZLIB_EXPECTED_SHA256 GET "${TLBX_ZLIB_PROOF}" archiveSha256)
    file(SHA256 "${CURRENT_INSTALLED_DIR}/lib/${TLBX_ZLIB_ARCHIVE}" TLBX_ZLIB_ACTUAL_SHA256)
    if(NOT TLBX_ZLIB_ACTUAL_SHA256 STREQUAL TLBX_ZLIB_EXPECTED_SHA256)
        message(FATAL_ERROR "Native link input differs from the reviewed patched zlib archive")
    endif()
    set(TLBX_BACKPORTS "{\"zlib\":${TLBX_ZLIB_PROOF}}")
endif()
file(WRITE "${CURRENT_PACKAGES_DIR}/share/libdatachannel/tlbx-link.json"
    "{\"version\":1,\"runtimeIdentifier\":\"${TARGET_TRIPLET}\",\"entryPoint\":\"${TLBX_NATIVE_ENTRY}\",\"entryPointSha256\":\"${TLBX_NATIVE_SHA256}\",\"backports\":${TLBX_BACKPORTS}}\n")
