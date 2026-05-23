project(jalium.native.media.core)

set(HEADERS
    include/jalium_media.h
    include/jalium_media_internal.h
    include/jalium_audio.h
    src/audio/audio_internal.h
)

# .core only ships the cross-platform helpers + status string + the audio
# ABI/internal headers. The audio TUs themselves are compiled INTO the
# per-platform `jalium.native.media.{windows,android,apple,linux}` library so
# the audio symbols land in the same DLL as video/image/camera. The list of
# audio sources is published as a root-level cache variable (see root
# CMakeLists.txt) so all platforms reference the same canonical files.
set(SOURCES
    src/jalium_media_common.cpp
)

add_library(${PROJECT_NAME} ${JALIUM_LIB_TYPE} ${HEADERS} ${SOURCES})

target_include_directories(${PROJECT_NAME}
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
)

if(NOT JALIUM_BUILD_STATIC)
    target_compile_definitions(${PROJECT_NAME}
        PRIVATE
            JALIUM_MEDIA_EXPORTS
    )
endif()

set_target_properties(${PROJECT_NAME} PROPERTIES
    OUTPUT_NAME "jalium.native.media.core"
    DEBUG_POSTFIX ""
)
