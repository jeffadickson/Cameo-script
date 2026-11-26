cmake_minimum_required(VERSION 3.22)
project(cameo_embed_vm VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_library(cameo_embed_vm INTERFACE)
add_library(cameo::embed_vm ALIAS cameo_embed_vm)
target_include_directories(cameo_embed_vm INTERFACE "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_features(cameo_embed_vm INTERFACE cxx_std_20)

option(CAMEO_EMBED_BUILD_TESTS "Build Cameo.Embed static C++20 VM host tests" ON)
if(CAMEO_EMBED_BUILD_TESTS)
    enable_testing()
    add_executable(cameo_embed_unit_tests tests/cameo_embed_unit_tests.cpp)
    target_link_libraries(cameo_embed_unit_tests PRIVATE cameo_embed_vm)
    target_compile_options(cameo_embed_unit_tests PRIVATE
        -fno-exceptions -fno-rtti -Wall -Wextra -Werror -pedantic)
    add_test(NAME cameo_embed_unit_tests COMMAND cameo_embed_unit_tests)

    add_executable(cameo_embed_runner tools/cameo_embed_runner.cpp)
    target_link_libraries(cameo_embed_runner PRIVATE cameo_embed_vm)
    target_compile_definitions(cameo_embed_runner PRIVATE CAMEO_EMBED_ENABLE_FLOAT=1 CAMEO_EMBED_ENABLE_MATH=1)
    target_compile_options(cameo_embed_runner PRIVATE
        -fno-exceptions -fno-rtti -Wall -Wextra -Werror -pedantic)
    target_link_libraries(cameo_embed_runner PRIVATE m)
endif()
