cmake_minimum_required(VERSION 3.20)

project(TigerClawSentenceNative LANGUAGES C CXX)

set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(GGML_STATIC ON CACHE BOOL "" FORCE)
set(GGML_BACKEND_DL OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_COMMON OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_APP OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_UI OFF CACHE BOOL "" FORCE)

add_subdirectory(
    "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/llama.cpp"
    "${CMAKE_CURRENT_BINARY_DIR}/llama.cpp")

add_library(TigerClaw.Sentence.Native SHARED
    SentenceQwenNative.cpp
    SentenceQwenNative.h)

target_compile_features(TigerClaw.Sentence.Native PRIVATE cxx_std_17)
target_compile_definitions(TigerClaw.Sentence.Native PRIVATE TIGERCLAW_SENTENCE_NATIVE_EXPORTS)
target_include_directories(TigerClaw.Sentence.Native PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/llama.cpp/include")
target_link_libraries(TigerClaw.Sentence.Native PRIVATE llama)

if(MSVC)
    target_compile_options(TigerClaw.Sentence.Native PRIVATE /utf-8 /EHsc)
endif()

set_target_properties(TigerClaw.Sentence.Native PROPERTIES
    OUTPUT_NAME "TigerClaw.Sentence.Native")

# The named-pipe sentence host is part of the Windows Core deployment. macOS
# embeds the shared scorer dylib in the IMK bundle, so do not compile this
# Windows.h-dependent executable on Apple platforms.
if(WIN32)
    add_executable(TigerClaw.Sentence.Host WIN32
        SentenceHost.cpp
        SentenceQwenNative.cpp
        SentenceQwenNative.h)

    target_compile_features(TigerClaw.Sentence.Host PRIVATE cxx_std_17)
    target_compile_definitions(TigerClaw.Sentence.Host PRIVATE TIGERCLAW_SENTENCE_NATIVE_STATIC)
    target_include_directories(TigerClaw.Sentence.Host PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/llama.cpp/include"
        "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/llama.cpp/vendor")
    target_link_libraries(TigerClaw.Sentence.Host PRIVATE llama shell32)

    if(MSVC)
        target_compile_options(TigerClaw.Sentence.Host PRIVATE /utf-8 /EHsc)
    endif()

    set_target_properties(TigerClaw.Sentence.Host PROPERTIES
        OUTPUT_NAME "TigerClaw.Sentence")
endif()
