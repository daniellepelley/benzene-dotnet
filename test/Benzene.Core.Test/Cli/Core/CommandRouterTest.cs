using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Benzene.CodeGen.Cli.Core;
using Benzene.CodeGen.Cli.Core.Parsing;
using Moq;
using Xunit;

namespace Benzene.Test.Cli.Core
{
    public class CommandRouterTest
    {
        [Fact]
        public async Task RouteAsync_MatchingCommandName_ExecutesThatCommand()
        {
            var command = new Mock<ICommand>();
            command.Setup(x => x.Name).Returns("build");
            command.Setup(x => x.ExecuteAsync(It.IsAny<CommandArguments>())).Returns(Task.CompletedTask);

            var router = new CommandRouter(command.Object);
            var args = new CommandArguments { Name = "build", Attributes = new Dictionary<string, string?>() };

            await router.RouteAsync(args);

            command.Verify(x => x.ExecuteAsync(args), Times.Once);
        }

        [Fact]
        public async Task RouteAsync_UnknownCommandName_PrintsHelpThenThrows()
        {
            // Phase 2: an unknown command must fail the process (real exit codes), not just log and
            // return as if nothing happened - so this now prints help and throws instead of quietly
            // writing to stderr and returning a completed task.
            var router = new CommandRouter();
            var args = new CommandArguments { Name = "does-not-exist", Attributes = new Dictionary<string, string?>() };

            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var capturedOut = new StringWriter();
            using var capturedError = new StringWriter();
            Console.SetOut(capturedOut);
            Console.SetError(capturedError);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => router.RouteAsync(args));
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }

            Assert.Contains("does-not-exist", capturedError.ToString());
            Assert.Contains("Available commands:", capturedOut.ToString());
        }

        [Fact]
        public async Task RouteAsync_HelpCommandName_IsAlwaysRoutable()
        {
            var router = new CommandRouter();
            var args = new CommandArguments { Name = "help", Value = null, Attributes = new Dictionary<string, string?>() };

            var originalOut = Console.Out;
            using var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            try
            {
                await router.RouteAsync(args);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            Assert.Contains("Available commands:", capturedOut.ToString());
        }
    }
}
