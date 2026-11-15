cmake_minimum_required(VERSION 3.20)
project(AgentFlowWorkerC LANGUAGES C)

# The Worker runtime is static by default so external node packages do not need
# to ship a separate AgentFlow runtime DLL next to every executable.
add_library(AgentFlowWorker STATIC
    Src/AgentFlowWorker.c
    Src/AgentFlowNode.c
    Third_Party/Cjson/cJSON.c)

target_include_directories(AgentFlowWorker
    PUBLIC Include
    PRIVATE Third_Party/Cjson)

target_compile_definitions(AgentFlowWorker PRIVATE AGENTFLOW_WORKER_BUILD)
target_compile_features(AgentFlowWorker PUBLIC c_std_11)

if (WIN32)
    target_compile_definitions(AgentFlowWorker
        PRIVATE _CRT_SECURE_NO_WARNINGS
        PUBLIC AGENTFLOW_WORKER_STATIC)
    if (MSVC)
        target_compile_options(AgentFlowWorker PRIVATE /W4)
    endif()
else()
    find_package(Threads REQUIRED)
    target_link_libraries(AgentFlowWorker PUBLIC Threads::Threads m)
    target_compile_options(AgentFlowWorker PRIVATE -Wall -Wextra -Wpedantic)
endif()

install(TARGETS AgentFlowWorker
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib)
install(DIRECTORY Include/ DESTINATION include)
