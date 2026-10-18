cmake_minimum_required(VERSION 3.28)
project(streampilot_bridge VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# libobs debe compilarse antes desde una versión fijada (ver scripts/build-native.ps1).
# Pasar -DCMAKE_PREFIX_PATH=<ruta de instalación de libobs>.
find_package(libobs REQUIRED)

add_library(streampilot_bridge SHARED src/bridge.cpp)
target_include_directories(streampilot_bridge PUBLIC include)
target_compile_definitions(streampilot_bridge PRIVATE SP_BRIDGE_EXPORTS NOMINMAX)
target_link_libraries(streampilot_bridge PRIVATE OBS::libobs)

if(MSVC)
  target_compile_options(streampilot_bridge PRIVATE /W4 /WX /utf-8)
endif()

install(TARGETS streampilot_bridge RUNTIME DESTINATION bin)
