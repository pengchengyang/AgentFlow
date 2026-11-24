// Quick SDK packaging orchestrator.
//
// This tool intentionally does not reuse package-sdk-dll.py. It performs a complete,
// fresh rebuild of every SDK component and writes every build intermediate, cache,
// staged file, and final archive under artifacts-quick.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cctype>
#include <cstdlib>
#include <cstring>
#include <iterator>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <mutex>
#include <optional>
#include <regex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

namespace fs = std::filesystem;

namespace {

std::mutex g_logMutex;

const std::vector<std::string> kSharedProjects = {
    "AgentFlow.Contracts",
    "AgentFlow.Core",
    "BroadcastKit",
    "BroadcastKit.Contracts",
    "AgentFlow.Shared",
    "Hosting.Logging",
};

const std::vector<std::string> kRequiredSharedAssemblies = {
    "AgentFlow.Contracts.dll",
    "AgentFlow.Core.dll",
    "BroadcastKit.dll",
    "BroadcastKit.Contracts.dll",
    "AgentFlow.Shared.dll",
    "Hosting.Logging.dll",
};

const std::vector<std::string> kDemoProjects = {
    "Demo.Cli",
    "Demo.Desktop",
};

std::string ToUtf8(const std::wstring& value) {
    if (value.empty()) {
        return {};
    }
    const int size = WideCharToMultiByte(
        CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
        nullptr, 0, nullptr, nullptr);
    if (size <= 0) {
        return {};
    }
    std::string result(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(
        CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
        result.data(), size, nullptr, nullptr);
    return result;
}

std::string NormalizeProcessOutput(const std::string& bytes) {
    if (bytes.empty()) {
        return {};
    }

    auto convert = [&](UINT codePage, DWORD flags) -> std::optional<std::string> {
        const int wideSize = MultiByteToWideChar(
            codePage, flags, bytes.c_str(), static_cast<int>(bytes.size()),
            nullptr, 0);
        if (wideSize <= 0) {
            return std::nullopt;
        }
        std::wstring wide(static_cast<size_t>(wideSize), L'\0');
        MultiByteToWideChar(
            codePage, flags, bytes.c_str(), static_cast<int>(bytes.size()),
            wide.data(), wideSize);
        return ToUtf8(wide);
    };

    if (auto utf8 = convert(CP_UTF8, MB_ERR_INVALID_CHARS)) {
        return *utf8;
    }
    if (auto ansi = convert(CP_ACP, 0)) {
        return *ansi;
    }
    return bytes;
}

void Log(const std::string& line) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    std::cout << line << '\n';
    std::cout.flush();
}

void LogBlock(const std::string& heading, const std::string& body) {
    if (body.empty()) {
        Log(heading);
        return;
    }
    std::string trimmed = body;
    while (!trimmed.empty() && (trimmed.back() == '\r' || trimmed.back() == '\n')) {
        trimmed.pop_back();
    }
    Log(heading + "\n" + trimmed);
}

std::wstring QuoteArg(const std::wstring& argument) {
    if (argument.empty()) {
        return L"\"\"";
    }

    bool needsQuotes = false;
    for (wchar_t ch : argument) {
        if (ch == L' ' || ch == L'\t' || ch == L'\n' || ch == L'\v' || ch == L'"') {
            needsQuotes = true;
            break;
        }
    }
    if (!needsQuotes) {
        return argument;
    }

    std::wstring result;
    result.push_back(L'"');
    size_t backslashes = 0;
    for (wchar_t ch : argument) {
        if (ch == L'\\') {
            ++backslashes;
            continue;
        }
        if (ch == L'"') {
            result.append(backslashes * 2 + 1, L'\\');
            result.push_back(L'"');
            backslashes = 0;
            continue;
        }
        result.append(backslashes, L'\\');
        backslashes = 0;
        result.push_back(ch);
    }
    result.append(backslashes * 2, L'\\');
    result.push_back(L'"');
    return result;
}

std::wstring JoinCommand(const fs::path& executable, const std::vector<std::wstring>& arguments) {
    std::wstring command = QuoteArg(executable.wstring());
    for (const auto& argument : arguments) {
        command += L' ';
        command += QuoteArg(argument);
    }
    return command;
}

struct ProcessResult {
    DWORD exitCode = 1;
    std::string output;
};

ProcessResult RunProcess(
    const fs::path& executable,
    const std::vector<std::wstring>& arguments,
    const fs::path& workingDirectory,
    const std::string& label,
    bool throwOnFailure = true) {
    const std::wstring commandLine = JoinCommand(executable, arguments);
    Log("  > [" + label + "] " + ToUtf8(commandLine));

    SECURITY_ATTRIBUTES security{};
    security.nLength = sizeof(security);
    security.bInheritHandle = TRUE;
    security.lpSecurityDescriptor = nullptr;

    HANDLE readPipe = nullptr;
    HANDLE writePipe = nullptr;
    if (!CreatePipe(&readPipe, &writePipe, &security, 0)) {
        throw std::runtime_error("CreatePipe failed for " + label);
    }
    SetHandleInformation(readPipe, HANDLE_FLAG_INHERIT, 0);

    HANDLE nulInput = CreateFileW(
        L"NUL", GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE, &security,
        OPEN_EXISTING, 0, nullptr);
    if (nulInput == INVALID_HANDLE_VALUE) {
        CloseHandle(readPipe);
        CloseHandle(writePipe);
        throw std::runtime_error("Unable to open NUL for child stdin.");
    }

    STARTUPINFOW startup{};
    startup.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;
    startup.hStdInput = nulInput;
    startup.hStdOutput = writePipe;
    startup.hStdError = writePipe;

    PROCESS_INFORMATION process{};
    const BOOL created = CreateProcessW(
        executable.wstring().c_str(),
        const_cast<LPWSTR>(commandLine.c_str()),
        nullptr, nullptr, TRUE, CREATE_NO_WINDOW,
        nullptr, workingDirectory.wstring().c_str(), &startup, &process);

    CloseHandle(nulInput);
    CloseHandle(writePipe);

    if (!created) {
        CloseHandle(readPipe);
        throw std::runtime_error(
            "CreateProcess failed for " + label + " (error " + std::to_string(GetLastError()) + ").");
    }

    std::string rawOutput;
    char buffer[4096];
    while (true) {
        DWORD bytesRead = 0;
        const BOOL read = ReadFile(readPipe, buffer, sizeof(buffer), &bytesRead, nullptr);
        if (!read || bytesRead == 0) {
            break;
        }
        rawOutput.append(buffer, buffer + bytesRead);
    }

    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exitCode = 1;
    GetExitCodeProcess(process.hProcess, &exitCode);

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    CloseHandle(readPipe);

    ProcessResult result;
    result.exitCode = exitCode;
    result.output = NormalizeProcessOutput(rawOutput);

    std::string status = "  [" + label + "] exit code " + std::to_string(exitCode);
    LogBlock(status, result.output);

    if (throwOnFailure && exitCode != 0) {
        throw std::runtime_error("Command failed: " + label + " (exit " + std::to_string(exitCode) + ").");
    }
    return result;
}

fs::path FindExecutable(const std::wstring& fileName, const fs::path& workingDirectory) {
    wchar_t found[MAX_PATH * 4]{};
    const DWORD length = SearchPathW(
        nullptr, fileName.c_str(), nullptr,
        static_cast<DWORD>(std::size(found)), found, nullptr);
    if (length > 0 && length < std::size(found)) {
        return fs::path(found);
    }

    const fs::path local = workingDirectory / fileName;
    if (fs::is_regular_file(local)) {
        return local;
    }
    throw std::runtime_error("Required executable was not found in PATH: " + ToUtf8(fileName));
}

std::vector<fs::path> FindAncestors(fs::path start) {
    std::vector<fs::path> result;
    if (start.empty()) {
        return result;
    }
    fs::path current = fs::absolute(start);
    if (!fs::is_directory(current)) {
        current = current.parent_path();
    }
    for (;;) {
        result.push_back(current);
        const fs::path parent = current.parent_path();
        if (parent == current) {
            break;
        }
        current = parent;
    }
    return result;
}

fs::path GetExecutableDirectory() {
    wchar_t modulePath[32768]{};
    if (GetModuleFileNameW(nullptr, modulePath, static_cast<DWORD>(std::size(modulePath)))) {
        return fs::path(modulePath).parent_path();
    }
    throw std::runtime_error("Unable to resolve QuickBuilder.exe directory.");
}

fs::path FindRepositoryRoot() {
    const fs::path executableDirectory = GetExecutableDirectory();
    std::vector<fs::path> candidates = FindAncestors(fs::current_path());
    const auto executableCandidates = FindAncestors(executableDirectory);
    candidates.insert(candidates.end(), executableCandidates.begin(), executableCandidates.end());

    for (const auto& candidate : candidates) {
        if (fs::is_regular_file(candidate / "package-sdk-dll.py") &&
            fs::is_directory(candidate / "Plugins") &&
            fs::is_directory(candidate / "AgentFlow.Contracts")) {
            return fs::weakly_canonical(candidate);
        }
    }
    throw std::runtime_error("Unable to locate the AgentFlow repository root.");
}

std::string ReadTextFile(const fs::path& path) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        throw std::runtime_error("Unable to read file: " + path.string());
    }
    std::ostringstream buffer;
    buffer << stream.rdbuf();
    return buffer.str();
}

void WriteTextFile(const fs::path& path, const std::string& content) {
    fs::create_directories(path.parent_path());
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) {
        throw std::runtime_error("Unable to write file: " + path.string());
    }
    stream.write(content.data(), static_cast<std::streamsize>(content.size()));
}

void ClearReadOnlyAttributes(const fs::path& path) {
    SetFileAttributesW(path.wstring().c_str(), FILE_ATTRIBUTE_NORMAL);
    std::error_code iteratorError;
    for (auto iterator = fs::recursive_directory_iterator(
             path, fs::directory_options::skip_permission_denied, iteratorError);
         iterator != fs::recursive_directory_iterator(); iterator.increment(iteratorError)) {
        if (iteratorError) {
            iteratorError.clear();
            continue;
        }
        SetFileAttributesW(iterator->path().wstring().c_str(), FILE_ATTRIBUTE_NORMAL);
    }
}

std::wstring ToExtendedLengthPath(const fs::path& path) {
    const std::wstring value = fs::absolute(path).lexically_normal().wstring();
    if (value.rfind(L"\\\\?\\", 0) == 0) {
        return value;
    }
    if (value.rfind(L"\\\\", 0) == 0) {
        return L"\\\\?\\UNC\\" + value.substr(2);
    }
    return L"\\\\?\\" + value;
}

bool RemovePathNative(const fs::path& path, std::string& failure) {
    const std::wstring extended = ToExtendedLengthPath(path);
    const DWORD attributes = GetFileAttributesW(extended.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        const DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) {
            return true;
        }
        failure = "GetFileAttributesW failed with error " + std::to_string(error);
        return false;
    }

    SetFileAttributesW(extended.c_str(), FILE_ATTRIBUTE_NORMAL);
    if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
        if (DeleteFileW(extended.c_str())) {
            return true;
        }
        const DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) {
            return true;
        }
        failure = "DeleteFileW failed with error " + std::to_string(error);
        return false;
    }

    WIN32_FIND_DATAW entry{};
    const std::wstring pattern = extended + L"\\*";
    HANDLE iterator = FindFirstFileW(pattern.c_str(), &entry);
    if (iterator == INVALID_HANDLE_VALUE) {
        const DWORD error = GetLastError();
        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) {
            failure = "FindFirstFileW failed with error " + std::to_string(error);
            return false;
        }
    }
    else {
        do {
            const std::wstring name = entry.cFileName;
            if (name != L"." && name != L"..") {
                if (!RemovePathNative(path / name, failure)) {
                    FindClose(iterator);
                    return false;
                }
            }
        } while (FindNextFileW(iterator, &entry));
        const DWORD error = GetLastError();
        FindClose(iterator);
        if (error != ERROR_NO_MORE_FILES) {
            failure = "FindNextFileW failed with error " + std::to_string(error);
            return false;
        }
    }

    SetFileAttributesW(extended.c_str(), FILE_ATTRIBUTE_NORMAL);
    if (RemoveDirectoryW(extended.c_str())) {
        return true;
    }
    const DWORD error = GetLastError();
    if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) {
        return true;
    }
    failure = "RemoveDirectoryW failed with error " + std::to_string(error);
    return false;
}

void RemoveAllWithRetry(const fs::path& path, int attempts = 30) {
    if (!fs::exists(path)) {
        return;
    }
    std::string lastFailure;
    for (int attempt = 0; attempt < attempts; ++attempt) {
        std::string nativeFailure;
        if (RemovePathNative(path, nativeFailure) && !fs::exists(path)) {
            return;
        }
        if (!nativeFailure.empty()) {
            lastFailure = nativeFailure;
        }

        ClearReadOnlyAttributes(path);
        std::error_code error;
        fs::remove_all(path, error);
        if (!error && !fs::exists(path)) {
            return;
        }
        if (error) {
            lastFailure = error.message();
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(300 + attempt * 100));
    }
    if (fs::exists(path)) {
        std::string detail = "Unable to remove directory after retries: " + path.string();
        if (!lastFailure.empty()) {
            detail += " (" + lastFailure + ")";
        }
        throw std::runtime_error(detail);
    }
}

void CopyDirectoryContents(const fs::path& source, const fs::path& destination) {
    if (!fs::is_directory(source)) {
        throw std::runtime_error("Directory does not exist: " + source.string());
    }
    fs::create_directories(destination);
    for (const auto& entry : fs::directory_iterator(source)) {
        const fs::path target = destination / entry.path().filename();
        if (entry.is_directory()) {
            fs::copy(
                entry.path(), target,
                fs::copy_options::recursive | fs::copy_options::overwrite_existing);
        } else {
            fs::copy_file(entry.path(), target, fs::copy_options::overwrite_existing);
        }
    }
}

void ReplaceDirectoryWithCopy(const fs::path& source, const fs::path& destination) {
    if (!fs::is_directory(source)) {
        throw std::runtime_error("Directory does not exist: " + source.string());
    }
    RemoveAllWithRetry(destination);
    fs::create_directories(destination.parent_path());
    fs::copy(source, destination, fs::copy_options::recursive);
}

void CopyMatchingFiles(
    const fs::path& source,
    const fs::path& destination,
    const std::string& extension,
    const std::optional<std::string>& exclude = std::nullopt) {
    fs::create_directories(destination);
    if (!fs::is_directory(source)) {
        throw std::runtime_error("Directory does not exist: " + source.string());
    }
    for (const auto& entry : fs::directory_iterator(source)) {
        if (!entry.is_regular_file() || entry.path().extension() != extension) {
            continue;
        }
        if (exclude && entry.path().filename() == *exclude) {
            continue;
        }
        fs::copy_file(
            entry.path(), destination / entry.path().filename(),
            fs::copy_options::overwrite_existing);
    }
}

std::vector<std::string> DiscoverPluginNames(const fs::path& pluginsRoot) {
    std::vector<std::string> names;
    if (!fs::is_directory(pluginsRoot)) {
        throw std::runtime_error("Plugins directory was not found: " + pluginsRoot.string());
    }
    for (const auto& entry : fs::directory_iterator(pluginsRoot)) {
        if (!entry.is_directory()) {
            continue;
        }
        const auto project = entry.path() / (entry.path().filename().string() + ".csproj");
        if (fs::is_regular_file(project)) {
            names.push_back(entry.path().filename().string());
        }
    }
    std::sort(names.begin(), names.end(), [](const auto& left, const auto& right) {
        return _stricmp(left.c_str(), right.c_str()) < 0;
    });
    return names;
}

std::vector<std::string> DiscoverExternalPackageIds(const fs::path& externalRoot) {
    std::vector<std::string> ids;
    if (!fs::is_directory(externalRoot)) {
        return ids;
    }

    const std::unordered_set<std::string> ignoredDirectories = {
        "build", "dist", "publish", "__pycache__",
        "win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-arm",
    };
    const std::regex packagePattern(
        R"(\"packageId\"\s*:\s*\"([^\"]+)\")",
        std::regex::ECMAScript);

    for (auto iterator = fs::recursive_directory_iterator(externalRoot);
         iterator != fs::recursive_directory_iterator(); ++iterator) {
        const auto& entry = *iterator;
        if (entry.is_directory()) {
            if (ignoredDirectories.find(entry.path().filename().string()) != ignoredDirectories.end()) {
                iterator.disable_recursion_pending();
            }
            continue;
        }
        if (!entry.is_regular_file() || entry.path().filename() != "plugin.json") {
            continue;
        }
        const std::string text = ReadTextFile(entry.path());
        std::smatch match;
        if (!std::regex_search(text, match, packagePattern) || match.size() < 2) {
            throw std::runtime_error("plugin.json does not declare packageId: " + entry.path().string());
        }
        ids.push_back(match[1].str());
    }

    std::sort(ids.begin(), ids.end(), [](const auto& left, const auto& right) {
        return _stricmp(left.c_str(), right.c_str()) < 0;
    });
    return ids;
}

void CopyDemoProject(const fs::path& source, const fs::path& destination) {
    if (!fs::is_directory(source)) {
        throw std::runtime_error("Demo project was not found: " + source.string());
    }
    RemoveAllWithRetry(destination);
    fs::create_directories(destination);

    const std::unordered_set<std::string> ignored = {"bin", "obj", "__pycache__"};
    for (auto iterator = fs::recursive_directory_iterator(source);
         iterator != fs::recursive_directory_iterator(); ++iterator) {
        const auto& entry = *iterator;
        const std::string name = entry.path().filename().string();
        if (entry.is_directory() && ignored.find(name) != ignored.end()) {
            iterator.disable_recursion_pending();
            continue;
        }
        const fs::path relative = fs::relative(entry.path(), source);
        const fs::path target = destination / relative;
        if (entry.is_directory()) {
            fs::create_directories(target);
        } else {
            fs::create_directories(target.parent_path());
            fs::copy_file(entry.path(), target, fs::copy_options::overwrite_existing);
        }
    }
}

std::string TrimAscii(const std::string& value) {
    size_t begin = 0;
    while (begin < value.size() && std::isspace(static_cast<unsigned char>(value[begin]))) {
        ++begin;
    }
    size_t end = value.size();
    while (end > begin && std::isspace(static_cast<unsigned char>(value[end - 1]))) {
        --end;
    }
    return value.substr(begin, end - begin);
}

std::string QueryMsBuildProperty(
    const fs::path& dotnet,
    const fs::path& repositoryRoot,
    const fs::path& project,
    const std::string& propertyName) {
    ProcessResult result = RunProcess(
        dotnet,
        {
            L"msbuild", project.wstring(), L"-nologo",
            L"-nodeReuse:false",
            L"-getProperty:" + std::wstring(propertyName.begin(), propertyName.end()),
        },
        repositoryRoot,
        "property " + propertyName,
        false);
    if (result.exitCode != 0) {
        throw std::runtime_error("Failed to query MSBuild property " + propertyName + ".");
    }
    const std::string value = TrimAscii(result.output);
    if (value.empty()) {
        throw std::runtime_error("MSBuild property is empty: " + propertyName);
    }
    return value;
}

struct Job {
    std::string name;
    std::function<void()> action;
};

int RunJobs(std::vector<Job> jobs, size_t requestedParallelism) {
    if (jobs.empty()) {
        return 0;
    }

    std::atomic<size_t> next{0};
    std::atomic<bool> stop{false};
    std::atomic<int> failures{0};
    const size_t workers = std::max<size_t>(
        1, std::min<size_t>(jobs.size(), requestedParallelism));

    std::vector<std::thread> threads;
    for (size_t worker = 0; worker < workers; ++worker) {
        threads.emplace_back([&]() {
            for (;;) {
                if (stop.load(std::memory_order_acquire)) {
                    return;
                }
                const size_t index = next.fetch_add(1, std::memory_order_acq_rel);
                if (index >= jobs.size()) {
                    return;
                }
                const Job& job = jobs[index];
                Log("  [start] " + job.name);
                try {
                    job.action();
                    Log("  [done ] " + job.name);
                } catch (const std::exception& error) {
                    stop.store(true, std::memory_order_release);
                    failures.fetch_add(1, std::memory_order_acq_rel);
                    Log("  [FAIL ] " + job.name + ": " + error.what());
                } catch (...) {
                    stop.store(true, std::memory_order_release);
                    failures.fetch_add(1, std::memory_order_acq_rel);
                    Log("  [FAIL ] " + job.name + ": unknown error");
                }
            }
        });
    }

    for (auto& thread : threads) {
        thread.join();
    }
    return failures.load(std::memory_order_acquire);
}

std::vector<std::wstring> DotnetBuildArguments(
    const fs::path& project,
    const std::string& configuration,
    const fs::path& output,
    const fs::path& artifactsPath) {
    return {
        L"build", project.wstring(),
        L"-c", std::wstring(configuration.begin(), configuration.end()),
        L"-p:Platform=x64",
        L"-p:CopyLocalLockFileAssemblies=true",
        L"-p:UseSharedCompilation=false",
        L"-p:AgentFlowExternalNodeEnabled=false",
        L"-o", output.wstring(),
        L"-v:minimal",
        L"--no-incremental",
        L"--disable-build-servers",
        L"--artifacts-path", artifactsPath.wstring(),
    };
}

std::vector<std::wstring> DotnetPublishArguments(
    const fs::path& project,
    const std::string& configuration,
    const fs::path& output,
    const fs::path& artifactsPath) {
    return {
        L"publish", project.wstring(),
        L"-c", std::wstring(configuration.begin(), configuration.end()),
        L"-p:Platform=x64",
        L"-p:CopyLocalLockFileAssemblies=true",
        L"-p:UseSharedCompilation=false",
        L"-p:AgentFlowExternalNodeEnabled=false",
        L"-o", output.wstring(),
        L"-v:minimal",
        L"--disable-build-servers",
        L"--artifacts-path", artifactsPath.wstring(),
    };
}

std::vector<std::wstring> NativeHostBuildArguments(
    const fs::path& project,
    const std::string& configuration,
    const fs::path& output,
    const fs::path& artifactsPath,
    const fs::path& codegenAssembly,
    const fs::path& proxyOutput) {
    std::vector<std::wstring> arguments =
        DotnetBuildArguments(project, configuration, output, artifactsPath);
    arguments.push_back(L"-p:NativeNodeCodeGenAssembly=" + codegenAssembly.wstring());
    arguments.push_back(L"-p:NativeNodeProxyOutput=" + proxyOutput.wstring());
    return arguments;
}

std::vector<std::wstring> NativeBuildArguments(
    const fs::path& script,
    const std::string& configuration,
    const fs::path& buildDirectory,
    const fs::path& outputDirectory,
    const fs::path& stateDirectory) {
    return {
        L"-NoProfile",
        L"-ExecutionPolicy", L"Bypass",
        L"-File", script.wstring(),
        L"-Configuration", std::wstring(configuration.begin(), configuration.end()),
        L"-BuildDir", buildDirectory.wstring(),
        L"-OutputDir", outputDirectory.wstring(),
        L"-StateDir", stateDirectory.wstring(),
        L"-Force",
    };
}

std::vector<std::wstring> ExternalBuildArguments(
    const fs::path& script,
    const std::string& packageId,
    const std::string& configuration,
    const std::string& rid,
    const fs::path& artifactsRoot) {
    const std::wstring widePackage(packageId.begin(), packageId.end());
    const std::wstring wideConfiguration(configuration.begin(), configuration.end());
    return {
        script.wstring(), L"build",
        L"--package", widePackage,
        L"--config", wideConfiguration,
        L"--rid", std::wstring(rid.begin(), rid.end()),
        L"--artifacts-root", artifactsRoot.wstring(),
        L"--fail-fast",
    };
}

std::vector<std::wstring> ExternalVerifyArguments(
    const fs::path& script,
    const std::string& configuration,
    const std::string& rid,
    const fs::path& artifactsRoot) {
    return {
        script.wstring(), L"verify",
        L"--all",
        L"--config", std::wstring(configuration.begin(), configuration.end()),
        L"--rid", std::wstring(rid.begin(), rid.end()),
        L"--artifacts-root", artifactsRoot.wstring(),
        L"--fail-fast",
    };
}

std::string PowerShellLiteral(const fs::path& path) {
    std::string value = path.string();
    std::string result;
    for (char ch : value) {
        if (ch == '\'') {
            result += "''";
        } else {
            result.push_back(ch);
        }
    }
    return result;
}

void ValidateDirectoryInsideRoot(const fs::path& root, const fs::path& candidate) {
    const fs::path canonicalRoot = fs::weakly_canonical(root);
    const fs::path parent = fs::weakly_canonical(candidate.parent_path());
    const fs::path relative = parent.lexically_relative(canonicalRoot);
    if (relative.empty() || relative.native().rfind(L"..", 0) == 0) {
        throw std::runtime_error("Refusing to clean a path outside the allowed root: " + candidate.string());
    }
}

void ConfigureBuildEnvironment(const fs::path& artifactsRoot) {
    const fs::path temp = artifactsRoot / "temp";
    const fs::path nuget = artifactsRoot / "nuget-packages";
    const fs::path dotnetHome = artifactsRoot / "dotnet-home";
    const fs::path pipCache = artifactsRoot / "pip-cache";
    const fs::path pycache = artifactsRoot / "python-pycache";

    for (const auto& directory : {temp, nuget, dotnetHome, pipCache, pycache}) {
        fs::create_directories(directory);
    }

    SetEnvironmentVariableW(L"TEMP", temp.wstring().c_str());
    SetEnvironmentVariableW(L"TMP", temp.wstring().c_str());
    SetEnvironmentVariableW(L"NUGET_PACKAGES", nuget.wstring().c_str());
    SetEnvironmentVariableW(L"DOTNET_CLI_HOME", dotnetHome.wstring().c_str());
    SetEnvironmentVariableW(L"PIP_CACHE_DIR", pipCache.wstring().c_str());
    SetEnvironmentVariableW(L"PYTHONPYCACHEPREFIX", pycache.wstring().c_str());
    SetEnvironmentVariableW(L"DOTNET_CLI_TELEMETRY_OPTOUT", L"1");
    SetEnvironmentVariableW(L"DOTNET_NOLOGO", L"1");
    SetEnvironmentVariableW(L"DOTNET_CLI_UI_LANGUAGE", L"en");
    SetEnvironmentVariableW(L"VSLANG", L"1033");
    SetEnvironmentVariableW(L"MSBUILDDISABLENODEREUSE", L"1");
    SetEnvironmentVariableW(L"DOTNET_CLI_USE_MSBUILD_SERVER", L"0");
    SetEnvironmentVariableW(L"PIP_DISABLE_PIP_VERSION_CHECK", L"1");
    SetEnvironmentVariableW(L"PIP_NO_INPUT", L"1");
}

void ValidateSharedAssemblies(const fs::path& configurationDirectory) {
    for (const auto& name : kRequiredSharedAssemblies) {
        const fs::path file = configurationDirectory / name;
        if (!fs::is_regular_file(file)) {
            throw std::runtime_error("Required Shared assembly is missing: " + file.string());
        }
    }
}

void FinalizeSharedAssemblies(
    const fs::path& buildRoot,
    const fs::path& staging,
    const std::string& configuration) {
    const fs::path destination = staging / "SharedDlls" / "x64" / configuration;
    fs::create_directories(destination);
    for (const auto& project : kSharedProjects) {
        const fs::path output = buildRoot / "Shared" / configuration / project;
        CopyMatchingFiles(output, destination, ".dll");
        if (configuration == "Debug") {
            CopyMatchingFiles(output, destination, ".pdb");
        }
    }
    ValidateSharedAssemblies(destination);
}

void FinalizePlugins(
    const fs::path& buildRoot,
    const fs::path& staging,
    const std::string& configuration,
    const std::vector<std::string>& pluginNames) {
    const fs::path configurationDirectory = staging / "SharedDlls" / "x64" / configuration;
    const fs::path pluginDirectory = configurationDirectory / "Plugins";
    fs::create_directories(pluginDirectory);

    for (const auto& pluginName : pluginNames) {
        if (configuration == "Release" && pluginName == "Node.Broadcast.Demo") {
            continue;
        }
        const fs::path output = buildRoot / "Plugins" / configuration / pluginName;
        const fs::path pluginDll = output / (pluginName + ".dll");
        if (!fs::is_regular_file(pluginDll)) {
            throw std::runtime_error("Plugin assembly was not produced: " + pluginDll.string());
        }
        fs::copy_file(
            pluginDll, pluginDirectory / pluginDll.filename(),
            fs::copy_options::overwrite_existing);
        CopyMatchingFiles(
            output, configurationDirectory, ".dll", pluginDll.filename().string());
        if (configuration == "Debug") {
            const fs::path pdb = output / (pluginName + ".pdb");
            if (fs::is_regular_file(pdb)) {
                fs::copy_file(
                    pdb, pluginDirectory / pdb.filename(),
                    fs::copy_options::overwrite_existing);
            }
        }
    }
}

void FinalizeWindowsNative(
    const fs::path& buildRoot,
    const fs::path& staging,
    const std::string& configuration) {
    const fs::path nativeSource = buildRoot / "WindowsNative" / configuration / "win-x64";
    const fs::path nativeDestination =
        staging / "SharedDlls" / "x64" / configuration / "Plugins" / "Windows" / "win-x64";
    ReplaceDirectoryWithCopy(nativeSource, nativeDestination);

    const fs::path nativeHostOutput = buildRoot / "NativeHost" / configuration;
    const fs::path nativeHostDll = nativeHostOutput / "AgentFlow.Windows.NativeHost.dll";
    if (!fs::is_regular_file(nativeHostDll)) {
        throw std::runtime_error("NativeHost assembly was not produced: " + nativeHostDll.string());
    }
    const fs::path pluginDirectory =
        staging / "SharedDlls" / "x64" / configuration / "Plugins";
    fs::create_directories(pluginDirectory);
    fs::copy_file(
        nativeHostDll, pluginDirectory / nativeHostDll.filename(),
        fs::copy_options::overwrite_existing);
    if (configuration == "Debug") {
        const fs::path pdb = nativeHostOutput / "AgentFlow.Windows.NativeHost.pdb";
        if (fs::is_regular_file(pdb)) {
            fs::copy_file(
                pdb, pluginDirectory / pdb.filename(),
                fs::copy_options::overwrite_existing);
        }
    }
}

void FinalizeNativeSdk(
    const fs::path& repositoryRoot,
    const fs::path& buildRoot,
    const fs::path& staging,
    const std::string& configuration) {
    const fs::path sourceHeaders =
        repositoryRoot / "Plugins" / "Windows" / "Src" / "AgentFlow.NativeNodeApi";
    const fs::path headerDestination = staging / "SDK" / "Windows" / "NativeNodeApi";
    fs::create_directories(headerDestination);
    for (const auto& name : {"agentflow_node_api.h", "agentflow_broadcast_helpers.h"}) {
        const fs::path source = sourceHeaders / name;
        if (!fs::is_regular_file(source)) {
            throw std::runtime_error("Windows native SDK header was not found: " + source.string());
        }
        fs::copy_file(
            source, headerDestination / source.filename(),
            fs::copy_options::overwrite_existing);
    }

    const fs::path codegenOutput = buildRoot / "NativeNodeCodeGen" / configuration;
    const fs::path codegenDestination =
        staging / "SDK" / "Windows" / "NativeNodeCodeGen" / configuration;
    ReplaceDirectoryWithCopy(codegenOutput, codegenDestination);
}

void FinalizeExternalPackages(
    const fs::path& artifactsRoot,
    const fs::path& staging,
    const std::string& configuration,
    const std::string& rid,
    const std::vector<std::string>& packageIds) {
    const fs::path sourceRoot = artifactsRoot / "External" / configuration / rid;
    const fs::path destination =
        staging / "SharedDlls" / "x64" / configuration / "Plugins" / "External" / rid;
    RemoveAllWithRetry(destination);
    fs::create_directories(destination);

    for (const auto& packageId : packageIds) {
        const fs::path source = sourceRoot / packageId;
        if (!fs::is_directory(source)) {
            throw std::runtime_error("External package output was not found: " + source.string());
        }
        ReplaceDirectoryWithCopy(source, destination / packageId);
    }
}

std::string BuildReadme(
    const std::string& targetFramework,
    const std::string& pluginFramework,
    const std::string& rid,
    bool externalEnabled) {
    std::ostringstream text;
    text << "AgentFlow Binary SDK\n\n";
    text << "This archive does not contain the AgentFlow platform source projects.\n";
    text << "It does include minimal Demo.Cli and Demo.Desktop source projects that run workflows.\n\n";
    text << "Layout:\n";
    text << "  SharedDlls/x64/Debug/Plugins          Debug C# node assemblies and PDB files\n";
    text << "  SharedDlls/x64/Debug/Plugins/External/<RID> Debug external-language node packages\n";
    text << "  SharedDlls/x64/Release                Release shared assemblies and dependencies\n";
    text << "  SharedDlls/x64/Release/Plugins        Release C# node assemblies\n";
    text << "  SharedDlls/x64/Release/Plugins/External/<RID> Verified Release external-language node packages\n";
    text << "  SharedDlls/x64/<Configuration>/Plugins/Windows/win-x64/<ModuleId>\n";
    text << "                                        Windows native node DLL and manifest\n";
    text << "  SDK/Windows/NativeNodeApi/            Stable C ABI and broadcast helper headers for Windows native nodes\n";
    text << "  SDK/Windows/NativeNodeCodeGen/<Configuration>\n";
    text << "                                        Proxy generator for Windows native node manifests\n";
    text << "  Demo.Cli/                          Minimal command-line workflow runner source project\n";
    text << "  Demo.Desktop/                      Minimal Avalonia workflow runner source project\n\n";
    text << "External projects should reference DLLs from the matching configuration:\n";
    text << "  Debug projects   -> SharedDlls/x64/Debug\n";
    text << "  Release projects -> SharedDlls/x64/Release\n\n";
    text << "Keep Plugins and SharedDlls from the same SDK archive. Do not mix Debug and Release.\n";
    text << "BinaryVersions.json contains the file and assembly versions of every AgentFlow-owned binary.\n";
    text << "Node.Broadcast.Demo is included only in the Debug plugin folder.\n";
    text << "External RIDs packaged: " << (externalEnabled ? rid : "none") << ".\n";
    text << "Release external packages were built, FakeCore-verified, and copied from artifacts/External/Release/<RID>.\n";
    text << "Windows native SDK content is included when the package is built on Windows.\n";
    text << "Application/shared target framework: " << targetFramework << ".\n";
    text << "Plugin target framework: " << pluginFramework << ".\n";
    return text.str();
}

void VerifyBinaryVersions(
    const fs::path& powershell,
    const fs::path& repositoryRoot,
    const fs::path& staging) {
    RunProcess(
        powershell,
        {
            L"-NoProfile", L"-ExecutionPolicy", L"Bypass",
            L"-File", (repositoryRoot / "Scripts" / "Verify-BinaryVersions.ps1").wstring(),
            L"-Root", staging.wstring(),
            L"-Output", (staging / "BinaryVersions.json").wstring(),
        },
        repositoryRoot,
        "Verify BinaryVersions");
}

void CreateZip(
    const fs::path& powershell,
    const fs::path& repositoryRoot,
    const fs::path& staging,
    const fs::path& zipPath) {
    if (fs::exists(zipPath)) {
        fs::remove(zipPath);
    }
    const std::string zipLiteral = PowerShellLiteral(zipPath);
    const std::string stagingLiteral = PowerShellLiteral(staging);
    const std::wstring escapedZip(zipLiteral.begin(), zipLiteral.end());
    const std::wstring escapedStaging(stagingLiteral.begin(), stagingLiteral.end());
    const std::wstring command =
        L"$ErrorActionPreference = 'Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem; "
        L"$zip = '" + escapedZip + L"'; "
        L"if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }; "
        L"[System.IO.Compression.ZipFile]::CreateFromDirectory('" + escapedStaging +
        L"', $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)";
    RunProcess(
        powershell,
        {L"-NoProfile", L"-ExecutionPolicy", L"Bypass", L"-Command", command},
        repositoryRoot,
        "Create SDK zip");
}

struct Options {
    bool skipExternal = false;
    std::string rid = "win-x64";
    size_t jobs = 0;
};

Options ParseOptions(int argc, wchar_t** argv) {
    Options options;
    for (int index = 1; index < argc; ++index) {
        const std::wstring argument = argv[index];
        const std::string utf8 = ToUtf8(argument);
        if (argument == L"--skip-external") {
            options.skipExternal = true;
        } else if (argument == L"--rid" && index + 1 < argc) {
            options.rid = ToUtf8(argv[++index]);
        } else if (argument == L"--jobs" && index + 1 < argc) {
            options.jobs = static_cast<size_t>(std::max(1, _wtoi(argv[++index])));
        } else if (argument == L"--help" || argument == L"-h") {
            std::cout
                << "QuickBuilder [--jobs N] [--rid win-x64] [--skip-external]\n"
                << "Performs a fresh SDK packaging build under artifacts-quick.\n";
            std::exit(0);
        } else {
            throw std::runtime_error("Unknown argument: " + utf8);
        }
    }
    if (options.jobs == 0) {
        const size_t hardware = std::thread::hardware_concurrency();
        options.jobs = std::clamp<size_t>(hardware == 0 ? 8 : hardware, 4, 16);
    }
    return options;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    const bool pauseOnExit = argc == 1 && GetConsoleWindow() != nullptr;
    try {
        SetConsoleOutputCP(CP_UTF8);
        const fs::path executableDirectory = GetExecutableDirectory();
        SetCurrentDirectoryW(executableDirectory.wstring().c_str());
        const Options options = ParseOptions(argc, argv);
        const fs::path repositoryRoot = FindRepositoryRoot();
        const fs::path artifactsRoot = executableDirectory / "artifacts-quick";
        const fs::path staging = artifactsRoot / "AgentFlow.Sdk.Binaries";
        const fs::path buildRoot = artifactsRoot / "sdk-dll-build";
        const fs::path zipPath = artifactsRoot / "AgentFlow.Sdk.Binaries.zip";

        ValidateDirectoryInsideRoot(executableDirectory, artifactsRoot);
        RemoveAllWithRetry(artifactsRoot);
        for (const auto& directory : {artifactsRoot, staging, buildRoot}) {
            fs::create_directories(directory);
        }
        ConfigureBuildEnvironment(artifactsRoot);

        const fs::path dotnet = FindExecutable(L"dotnet.exe", repositoryRoot);
        const fs::path powershell = FindExecutable(L"powershell.exe", repositoryRoot);
        const fs::path python = FindExecutable(L"python.exe", repositoryRoot);

        std::cout << "==========================================\n";
        std::cout << "AgentFlow binary SDK quick packaging\n";
        std::cout << "Root:      " << repositoryRoot.string() << '\n';
        std::cout << "Staging:   " << staging.string() << '\n';
        std::cout << "Output:    " << zipPath.string() << '\n';
        std::cout << "Parallel jobs: " << options.jobs << '\n';
        std::cout << "==========================================\n";

        const std::string targetFramework = QueryMsBuildProperty(
            dotnet, repositoryRoot,
            repositoryRoot / "AgentFlow.Contracts" / "AgentFlow.Contracts.csproj",
            "AgentFlowTargetFramework");
        const std::string pluginFramework = QueryMsBuildProperty(
            dotnet, repositoryRoot,
            repositoryRoot / "AgentFlow.Contracts" / "AgentFlow.Contracts.csproj",
            "PluginTargetFramework");

        const std::vector<std::string> pluginNames =
            DiscoverPluginNames(repositoryRoot / "Plugins");
        const std::vector<std::string> packageIds =
            DiscoverExternalPackageIds(repositoryRoot / "Plugins" / "External");

        std::vector<Job> jobs;
        for (const std::string configuration : {"Debug", "Release"}) {
            for (const auto& projectName : kSharedProjects) {
                const fs::path project =
                    repositoryRoot / projectName / (projectName + ".csproj");
                if (!fs::is_regular_file(project)) {
                    throw std::runtime_error("Shared project not found: " + project.string());
                }
                const fs::path output =
                    buildRoot / "Shared" / configuration / projectName;
                const fs::path artifacts =
                    buildRoot / "DotnetArtifacts" / "Shared" / configuration / projectName;
                jobs.push_back({
                    configuration + " shared " + projectName,
                    [=]() {
                        fs::create_directories(output);
                        fs::create_directories(artifacts);
                        RunProcess(
                            dotnet,
                            DotnetBuildArguments(project, configuration, output, artifacts),
                            repositoryRoot,
                            configuration + " shared " + projectName);
                    },
                });
            }

            for (const auto& pluginName : pluginNames) {
                if (configuration == "Release" && pluginName == "Node.Broadcast.Demo") {
                    continue;
                }
                const fs::path project =
                    repositoryRoot / "Plugins" / pluginName / (pluginName + ".csproj");
                const fs::path output =
                    buildRoot / "Plugins" / configuration / pluginName;
                const fs::path artifacts =
                    buildRoot / "DotnetArtifacts" / "Plugins" / configuration / pluginName;
                jobs.push_back({
                    configuration + " plugin " + pluginName,
                    [=]() {
                        fs::create_directories(output);
                        fs::create_directories(artifacts);
                        RunProcess(
                            dotnet,
                            DotnetBuildArguments(project, configuration, output, artifacts),
                            repositoryRoot,
                            configuration + " plugin " + pluginName);
                    },
                });
            }

            const fs::path nativeBuildDirectory =
                buildRoot / "WindowsNative" / configuration / "build";
            const fs::path nativeOutputDirectory =
                buildRoot / "WindowsNative" / configuration / "win-x64";
            const fs::path nativeStateDirectory =
                buildRoot / "WindowsNative" / configuration / "state";
            jobs.push_back({
                configuration + " Windows native nodes",
                [=]() {
                    fs::create_directories(nativeBuildDirectory);
                    fs::create_directories(nativeOutputDirectory);
                    fs::create_directories(nativeStateDirectory);
                    RunProcess(
                        powershell,
                        NativeBuildArguments(
                            repositoryRoot / "Scripts" / "build_windows_native.ps1",
                            configuration,
                            nativeBuildDirectory,
                            nativeOutputDirectory,
                            nativeStateDirectory),
                        repositoryRoot,
                        configuration + " Windows native nodes");
                },
            });

            const fs::path nativeHostProject =
                repositoryRoot / "Plugins" / "Windows" / "AgentFlow.Windows.NativeHost" /
                "AgentFlow.Windows.NativeHost.csproj";
            const fs::path nativeHostOutput = buildRoot / "NativeHost" / configuration;
            const fs::path nativeHostArtifacts =
                buildRoot / "DotnetArtifacts" / "NativeHost" / configuration;
            const fs::path codegenOutput =
                buildRoot / "NativeNodeCodeGen" / configuration;
            const fs::path codegenArtifacts =
                buildRoot / "DotnetArtifacts" / "NativeNodeCodeGen" / configuration;
            const fs::path nativeProxyOutput =
                buildRoot / "Generated" / "NativeHost" / configuration / "NativeNodeProxies.g.cs";
            jobs.push_back({
                configuration + " NativeHost",
                [=]() {
                    fs::create_directories(codegenOutput);
                    fs::create_directories(codegenArtifacts);
                    RunProcess(
                        dotnet,
                        DotnetPublishArguments(
                            repositoryRoot / "Tools" / "NativeNodeCodeGen" /
                            "NativeNodeCodeGen.csproj",
                            configuration, codegenOutput, codegenArtifacts),
                        repositoryRoot,
                        configuration + " NativeNodeCodeGen");
                    fs::create_directories(nativeHostOutput);
                    fs::create_directories(nativeHostArtifacts);
                    fs::create_directories(nativeProxyOutput.parent_path());
                    RunProcess(
                        dotnet,
                        NativeHostBuildArguments(
                            nativeHostProject, configuration,
                            nativeHostOutput, nativeHostArtifacts,
                            codegenOutput / "NativeNodeCodeGen.dll",
                            nativeProxyOutput),
                        repositoryRoot,
                        configuration + " NativeHost");
                },
            });

            if (!options.skipExternal) {
                for (const auto& packageId : packageIds) {
                    jobs.push_back({
                        configuration + " external " + packageId,
                        [=]() {
                            RunProcess(
                                python,
                                ExternalBuildArguments(
                                    repositoryRoot / "Scripts" / "build_external_nodes.py",
                                    packageId, configuration, options.rid, artifactsRoot),
                                repositoryRoot,
                                configuration + " external " + packageId);
                        },
                    });
                }
            }
        }

        Log("");
        Log("Starting fresh multi-threaded build graph.");
        if (RunJobs(std::move(jobs), options.jobs) != 0) {
            throw std::runtime_error("One or more build jobs failed.");
        }

        if (!options.skipExternal) {
            Log("");
            Log("Verifying Release external-language packages.");
            RunProcess(
                python,
                ExternalVerifyArguments(
                    repositoryRoot / "Scripts" / "build_external_nodes.py",
                    "Release", options.rid, artifactsRoot),
                repositoryRoot,
                "Verify external packages");
        }

        Log("");
        Log("Assembling SDK staging layout.");
        for (const std::string configuration : {"Debug", "Release"}) {
            FinalizeSharedAssemblies(buildRoot, staging, configuration);
            FinalizePlugins(buildRoot, staging, configuration, pluginNames);
            FinalizeWindowsNative(buildRoot, staging, configuration);
            FinalizeNativeSdk(repositoryRoot, buildRoot, staging, configuration);
            if (!options.skipExternal) {
                FinalizeExternalPackages(
                    artifactsRoot, staging, configuration,
                    options.rid, packageIds);
            }
        }

        for (const auto& projectName : kDemoProjects) {
            CopyDemoProject(
                repositoryRoot / "Samples" / projectName,
                staging / projectName);
        }

        VerifyBinaryVersions(powershell, repositoryRoot, staging);
        WriteTextFile(
            staging / "README.txt",
            BuildReadme(
                targetFramework, pluginFramework, options.rid,
                !options.skipExternal));
        CreateZip(powershell, repositoryRoot, staging, zipPath);

        Log("");
        Log("Package created: " + zipPath.string());
        if (pauseOnExit) {
            std::cout << "\nPress Enter to exit...";
            std::cout.flush();
            std::cin.get();
        }
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "ERROR: " << error.what() << '\n';
        if (pauseOnExit) {
            std::cout << "\nPress Enter to exit...";
            std::cout.flush();
            std::cin.get();
        }
        return 1;
    } catch (...) {
        std::cerr << "ERROR: unknown failure\n";
        if (pauseOnExit) {
            std::cout << "\nPress Enter to exit...";
            std::cout.flush();
            std::cin.get();
        }
        return 1;
    }
}
