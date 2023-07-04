/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using AWS.Lambda.Powertools.Idempotency;
using AWS.Lambda.Powertools.Logging;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace HelloWorld;

public class Function
{
    private static AmazonDynamoDBClient? _dynamoDbClient;

    /// <summary>
    /// Function constructor
    /// </summary>
    public Function()
    {
        _dynamoDbClient = new AmazonDynamoDBClient();

        Init(_dynamoDbClient);
    }

    /// <summary>
    /// Test constructor
    /// </summary>
    public Function(AmazonDynamoDBClient amazonDynamoDb)
    {
        _dynamoDbClient = amazonDynamoDb;
        Init(amazonDynamoDb);
    }

    private void Init(AmazonDynamoDBClient amazonDynamoDb)
    {
        ArgumentNullException.ThrowIfNull(amazonDynamoDb);
        var tableName = Environment.GetEnvironmentVariable("TABLE_NAME");
        ArgumentNullException.ThrowIfNull(tableName);

        Idempotency.Configure(builder =>
            builder
                .WithOptions(optionsBuilder =>
                    optionsBuilder
                        .WithEventKeyJmesPath("powertools_json(Body).address") // will retrieve the address field in the body which is a string transformed to json with `powertools_json`
                        .WithExpiration(TimeSpan.FromSeconds(10)))
                .UseDynamoDb(storeBuilder =>
                    storeBuilder
                        .WithTableName(tableName)
                        .WithDynamoDBClient(amazonDynamoDb)
                ));
    }

    /// <summary>
    /// Lambda Handler
    /// Try with:
    /// <pre>
    /// curl -X POST https://[REST-API-ID].execute-api.[REGION].amazonaws.com/Prod/hello/ -H "Content-Type: application/json" -d '{"address": "https://checkip.amazonaws.com"}'
    /// </pre>
    /// </summary>
    /// <param name="apigwProxyEvent">API Gateway Proxy event</param>
    /// <param name="context">AWS Lambda context</param>
    /// <returns>API Gateway Proxy response</returns>
    [Idempotent]
    [Logging(LogEvent = true)]
    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest apigwProxyEvent, ILambdaContext context)
    {
        var requestContextRequestId = apigwProxyEvent.RequestContext.RequestId;
        var response = new
        {
            RequestId = requestContextRequestId,
            Greeting = "Hello Powertools for AWS Lambda (.NET)",
            MethodGuid = GenerateGuid(), // Guid generated by the GenerateGuid method. used to compare Method output
            HandlerGuid = Guid.NewGuid().ToString() // Guid generated in the Handler. used to compare Handler output
        };

        try
        {
            return new APIGatewayProxyResponse
            {
                Body = JsonSerializer.Serialize(response),
                StatusCode = 200,
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }
        catch (Exception e)
        {
            return new APIGatewayProxyResponse
            {
                Body = e.Message,
                StatusCode = 500,
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }
    }

    /// <summary>
    /// Generates a new Guid to check if value is the same between calls (should be when idempotency enabled)
    /// </summary>
    /// <returns>GUID</returns>
    private static string GenerateGuid()
    {
        return Guid.NewGuid().ToString();
    }
}