cmake_minimum_required(VERSION 3.20)
project(AgentFlow.Windows.NativeNodes LANGUAGES CXX)

if(NOT WIN32)
  message(WARNING "Windows native nodes are only built on Windows.")
  return()
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(NATIVE_API_DIR "${CMAKE_CURRENT_SOURCE_DIR}/AgentFlow.NativeNodeApi")
set(NATIVE_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/Node.NativeUpperCase.Windows")
set(NATIVE_OUTPUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../win-x64/NativeUpperCase" CACHE PATH "Directory where Windows native node artifacts are written.")

add_library(Node.NativeUpperCase.Windows SHARED
  "${NATIVE_SOURCE_DIR}/NativeUpperCaseNode.cpp"
  "${NATIVE_SOURCE_DIR}/NativeUpperCaseNode.h"
  "${NATIVE_API_DIR}/agentflow_broadcast_helpers.h"
)

target_include_directories(Node.NativeUpperCase.Windows PRIVATE "${NATIVE_API_DIR}")
target_compile_definitions(Node.NativeUpperCase.Windows PRIVATE AGENTFLOW_NODE_API_BUILD UNICODE _UNICODE)
set_target_properties(Node.NativeUpperCase.Windows PROPERTIES
  OUTPUT_NAME "Node.NativeUpperCase.Windows"
  RUNTIME_OUTPUT_DIRECTORY "${NATIVE_OUTPUT_DIR}"
  RUNTIME_OUTPUT_DIRECTORY_DEBUG "${NATIVE_OUTPUT_DIR}"
  RUNTIME_OUTPUT_DIRECTORY_RELEASE "${NATIVE_OUTPUT_DIR}"
)

add_custom_command(TARGET Node.NativeUpperCase.Windows POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
          "${NATIVE_SOURCE_DIR}/manifest.json"
          "${NATIVE_OUTPUT_DIR}/manifest.json"
)

