// 引用Rolling.Logging类库的命名空间（和库项目名一致）
using Rolling.Logging;
using Serilog.Events;
using System;
using System.Threading;

// Demo项目的命名空间（和项目名Rolling.Logging.Demo匹配）
namespace Rolling.Logging.Demo
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("===== Rolling.Logging 组件测试开始 =====");
            Console.WriteLine("=======================================\n");

            try
            {
                // --------------- 测试1：默认配置日志 ---------------
                Console.WriteLine("[测试1] 默认配置日志（控制台+文件，级别Information）");
                RollingLogger.Info("这是默认配置的Info级日志");
                RollingLogger.Debug("这是Debug级日志（默认配置不输出，级别不够）");
                RollingLogger.Warn("这是Warn级日志，带参数：{Time}", DateTime.Now);
                Console.WriteLine("→ 查看控制台输出 + ./Logs/Rolling-.log 文件\n");
                Thread.Sleep(1000);

                // --------------- 测试2：自定义配置日志 ---------------
                Console.WriteLine("[测试2] 自定义配置日志（Debug级别+自定义格式+10MB单文件）");
                var customOptions = new RollingLoggerOptions
                {
                    MinimumLevel = LogEventLevel.Debug,
                    LogFilePath = "./Logs/Rolling-Demo-Test.log",
                    OutputTemplate = "[Rolling-{Timestamp:yyyy-MM-dd HH:mm:ss.fff}][{Level:u3}][{SourceContext}][{CallerFile}:{CallerLineNumber}] {Message:lj}{NewLine}{Exception}",
                    FileSizeLimitBytes = 1024 * 1024 * 10,
                    RetainedFileCountLimit = 5,
                    EnableConsole = true,
                    EnableFile = true,
                };
                RollingLogger.Initialize(customOptions);

                RollingLogger.Debug("Debug级日志（自定义配置已开启）");
                RollingLogger.Info("Info级日志，结构化参数：{Name}", "RollingDemo");
                RollingLogger.Warn("Warn级日志");
                RollingLogger.Error("Error级日志");
                Console.WriteLine("→ 查看控制台输出 + ./Logs/Rolling-Demo-Test.log 文件\n");
                Thread.Sleep(1000);

                // --------------- 测试3：实例化日志（带上下文） ---------------
                Console.WriteLine("[测试3] 实例化日志（带SourceContext，便于定位）");
                var loggerInstance = new RollingLogger(customOptions);
                loggerInstance.Info("Program.Main", "这是带上下文的Info日志（上下文：Program.Main）");
                loggerInstance.Debug("Program.Main", "调试上下文日志：{Action}", "测试实例化");
                Console.WriteLine("→ 日志中会显示 [SourceContext] = Program.Main\n");
                Thread.Sleep(1000);

                // --------------- 测试4：异常日志（带堆栈） ---------------
                Console.WriteLine("[测试4] 异常日志（包含完整堆栈信息）");
                throw new InvalidOperationException("模拟业务异常", new DivideByZeroException("内部异常"));
            }
            catch (Exception ex)
            {
                RollingLogger.Error(ex, "捕获到异常：{Message}", ex.Message);
                Console.WriteLine("→ 日志中会包含完整的异常堆栈信息\n");
            }
            finally
            {
                RollingLogger.Close();
                Console.WriteLine("=======================================");
                Console.WriteLine("===== Rolling.Logging 组件测试结束 =====");
                Console.WriteLine("✅ 验证项：");
                Console.WriteLine("  1. ./Logs 目录下是否生成日志文件；");
                Console.WriteLine("  2. 日志文件格式是否符合自定义模板；");
                Console.WriteLine("  3. 异常日志是否包含完整堆栈；");
                Console.WriteLine("  4. Debug级日志仅在自定义配置下输出。");
                Console.WriteLine("\n按任意键退出...");
                Console.ReadKey();
            }
        }
    }
}