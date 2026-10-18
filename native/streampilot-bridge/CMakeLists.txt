cmake_minimum_required(VERSION 3.28)
project(streampilot_bridge VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Dos formas de enlazar libobs:
#  1. OBS_INCLUDE_DIR + OBS_IMPORT_LIB: headers de un tag fijado + import lib generada desde obs.dll
#     (modo de desarrollo, ver scripts/build-native.ps1 -Mode Prebuilt).
#  2. find_package(libobs): libobs compilado desde fuente (-DCMAKE_PREFIX_PATH=<instalación>).
set(OBS_INCLUDE_DIR "" CACHE PATH "Directorio libobs/ con los headers de la versión fijada")
set(OBS_IMPORT_LIB "" CACHE FILEPATH "obs.lib generada desde obs.dll")

add_library(streampilot_bridge SHARED src/bridge.cpp)
target_include_directories(streampilot_bridge PUBLIC include)
target_compile_definitions(streampilot_bridge PRIVATE SP_BRIDGE_EXPORTS NOMINMAX _WIN32_WINNT=0x0A00)

if(OBS_INCLUDE_DIR AND OBS_IMPORT_LIB)
  # obsconfig.h lo genera el build de OBS; con headers sueltos lo generamos con los valores de Windows.
  set(OBS_RELEASE_CANDIDATE 0)
  set(OBS_BETA 0)
  configure_file("${OBS_INCLUDE_DIR}/obsconfig.h.in" "${CMAKE_CURRENT_BINARY_DIR}/obsgen/obsconfig.h")
  target_include_directories(streampilot_bridge SYSTEM PRIVATE "${OBS_INCLUDE_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/obsgen")
  target_link_libraries(streampilot_bridge PRIVATE "${OBS_IMPORT_LIB}")
else()
  find_package(libobs REQUIRED)
  target_link_libraries(streampilot_bridge PRIVATE OBS::libobs)
endif()

if(MSVC)
  target_compile_options(streampilot_bridge PRIVATE /W4 /WX /utf-8)
endif()
if(WIN32)
  target_link_libraries(streampilot_bridge PRIVATE rtworkq)
endif()

# Herramienta de diagnóstico del subsistema gráfico (no se distribuye).
add_executable(sp_diag tools/sp_diag.cpp)
get_target_property(_sp_obs_includes streampilot_bridge INCLUDE_DIRECTORIES)
target_include_directories(sp_diag PRIVATE ${_sp_obs_includes})
get_target_property(_sp_obs_links streampilot_bridge LINK_LIBRARIES)
target_link_libraries(sp_diag PRIVATE ${_sp_obs_links})
target_compile_definitions(sp_diag PRIVATE NOMINMAX)

install(TARGETS streampilot_bridge RUNTIME DESTINATION bin)
