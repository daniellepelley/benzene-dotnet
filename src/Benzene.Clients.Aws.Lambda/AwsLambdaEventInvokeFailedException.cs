using System;

namespace Benzene.Clients.Aws.Lambda
{
    /// <summary>
    /// Thrown when a <see cref="Amazon.Lambda.InvocationType.Event"/> (fire-and-forget) invocation returns
    /// a non-2xx <c>InvokeResponse.StatusCode</c> - i.e. the Invoke API itself rejected or failed to accept
    /// the invocation (for example a throttling or validation error surfaced synchronously), rather than
    /// the target function running asynchronously as intended. Raising this instead of silently treating
    /// the invoke as accepted lets the caller surface it as a failure result, mirroring
    /// <see cref="AwsLambdaFunctionErrorException"/> for the request/response case.
    /// </summary>
    public class AwsLambdaEventInvokeFailedException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AwsLambdaEventInvokeFailedException"/> class.
        /// </summary>
        /// <param name="functionName">The invoked function's name.</param>
        /// <param name="statusCode">The non-2xx <c>StatusCode</c> the Invoke API returned.</param>
        public AwsLambdaEventInvokeFailedException(string functionName, int statusCode)
            : base($"AWS Lambda Event invoke of '{functionName}' returned status code {statusCode}.")
        {
            FunctionName = functionName;
            StatusCode = statusCode;
        }

        /// <summary>Gets the invoked function's name.</summary>
        public string FunctionName { get; }

        /// <summary>Gets the non-2xx <c>StatusCode</c> the Invoke API returned.</summary>
        public int StatusCode { get; }
    }
}
