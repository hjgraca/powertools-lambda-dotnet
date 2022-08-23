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
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using AWS.Lambda.Powertools.Idempotency.Exceptions;
using AWS.Lambda.Powertools.Idempotency.Persistence;
using FluentAssertions;
using Xunit;

namespace AWS.Lambda.Powertools.Idempotency.Tests.Persistence;

public class DynamoDBPersistenceStoreTests : IntegrationTestBase
{
    private DynamoDBPersistenceStore _dynamoDbPersistenceStore;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _dynamoDbPersistenceStore = DynamoDBPersistenceStore
            .Builder()
            .WithTableName(TABLE_NAME)
            .WithDynamoDBClient(client)
            .Build();
        _dynamoDbPersistenceStore.Configure(IdempotencyConfig.Builder().Build(),functionName: null);
    }
    //putRecord
    [Fact]
    public async Task PutRecord_ShouldCreateRecordInDynamoDB()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long expiry = now.AddSeconds(3600).ToUnixTimeSeconds();
        await _dynamoDbPersistenceStore
            .PutRecord(new DataRecord("key", DataRecord.DataRecordStatus.COMPLETED, expiry, null, null), now);

        var key = CreateKey("key");

        var getItemResponse =
            await client.GetItemAsync(new GetItemRequest
            {
                TableName = TABLE_NAME,
                Key = key
            });

        var item = getItemResponse.Item;
        item.Should().NotBeNull();
        item["status"].S.Should().Be("COMPLETED");
        item["expiration"].N.Should().Be(expiry.ToString());
    }

    [Fact]
    public async Task PutRecord_ShouldThrowIdempotencyItemAlreadyExistsException_IfRecordAlreadyExist() 
    {
        var key = CreateKey("key");

        // GIVEN: Insert a fake item with same id
        Dictionary<String, AttributeValue> item = new(key);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long expiry = now.AddSeconds(30).ToUnixTimeMilliseconds();
        item.Add("expiration", new AttributeValue(){N = expiry.ToString()});
        item.Add("status", new AttributeValue(DataRecord.DataRecordStatus.COMPLETED.ToString()));
        item.Add("data", new AttributeValue("Fake Data"));
        await client.PutItemAsync(new PutItemRequest
        {
            TableName = TABLE_NAME,
            Item = item
        });

        // WHEN: call putRecord
        long expiry2 = now.AddSeconds(3600).ToUnixTimeSeconds();
        Func<Task> act = () => _dynamoDbPersistenceStore.PutRecord(
            new DataRecord("key",
                DataRecord.DataRecordStatus.INPROGRESS,
                expiry2,
                null,
                null
            ), now);
        await act.Should().ThrowAsync<IdempotencyItemAlreadyExistsException>();
        
        // THEN: item was not updated, retrieve the initial one
        Dictionary<String, AttributeValue> itemInDb = (await client.GetItemAsync(new GetItemRequest
            {
                TableName = TABLE_NAME,
                Key = key
            })).Item;
        itemInDb.Should().NotBeNull();
        itemInDb["status"].S.Should().Be("COMPLETED");
        itemInDb["expiration"].N.Should().Be(expiry.ToString());
        itemInDb["data"].S.Should().Be("Fake Data");
    }
    
    //getRecord
    [Fact]
    public async Task GetRecord_ShouldReturnExistingRecord()
    {
        var key = new DictionaryEntry();
        // GIVEN: Insert a fake item with same id
        Dictionary<String, AttributeValue> item = new()
        {
            {"id", new AttributeValue("key")} //key
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long expiry = now.AddSeconds(30).ToUnixTimeMilliseconds();
        item.Add("expiration", new AttributeValue
        {
            N = expiry.ToString()
        });
        item.Add("status", new AttributeValue(DataRecord.DataRecordStatus.COMPLETED.ToString()));
        item.Add("data", new AttributeValue("Fake Data"));
        var response = await client.PutItemAsync(new PutItemRequest()
        {
            TableName = TABLE_NAME,
            Item = item
        });

        // WHEN
        DataRecord record = await _dynamoDbPersistenceStore.GetRecord("key");

        // THEN
        record.IdempotencyKey.Should().Be("key");
        record.Status.Should().Be(DataRecord.DataRecordStatus.COMPLETED);
        record.ResponseData.Should().Be("Fake Data");
        record.ExpiryTimestamp.Should().Be(expiry);
    }

    [Fact]
    public async Task GetRecord_ShouldThrowException_WhenRecordIsAbsent()
    {
        Func<Task> act = () => _dynamoDbPersistenceStore.GetRecord("key");
        await act.Should().ThrowAsync<IdempotencyItemNotFoundException>();
    }
    //updateRecord

    [Fact]
    public async Task UpdateRecord_ShouldUpdateRecord()
    {
        // GIVEN: Insert a fake item with same id
        var key = CreateKey("key");
        Dictionary<String, AttributeValue> item = new(key);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long expiry = now.AddSeconds(360).ToUnixTimeMilliseconds();
        item.Add("expiration", new AttributeValue
        {
            N = expiry.ToString()
        });
        item.Add("status", new AttributeValue(DataRecord.DataRecordStatus.INPROGRESS.ToString()));
        await client.PutItemAsync(new PutItemRequest
        {
            TableName = TABLE_NAME,
            Item = item
        });
        // enable payload validation
        _dynamoDbPersistenceStore.Configure(IdempotencyConfig.Builder().WithPayloadValidationJmesPath("path").Build(),
            null);

        // WHEN
        expiry = now.AddSeconds(3600).ToUnixTimeMilliseconds();
        DataRecord record = new DataRecord("key", DataRecord.DataRecordStatus.COMPLETED, expiry, "Fake result", "hash");
        await _dynamoDbPersistenceStore.UpdateRecord(record);

        // THEN
        Dictionary<String, AttributeValue> itemInDb = (await client.GetItemAsync(new GetItemRequest
        {
            TableName = TABLE_NAME,
            Key = key
        })).Item;

        itemInDb["status"].S.Should().Be("COMPLETED");
        itemInDb["expiration"].N.Should().Be(expiry.ToString());
        itemInDb["data"].S.Should().Be("Fake result");
        itemInDb["validation"].S.Should().Be("hash");
    }

    //deleteRecord
    [Fact]
    public async Task DeleteRecord_ShouldDeleteRecord() 
    {
        // GIVEN: Insert a fake item with same id
        var key = CreateKey("key");
        Dictionary<String, AttributeValue> item = new(key);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long expiry = now.AddSeconds(360).ToUnixTimeMilliseconds();
        item.Add("expiration", new AttributeValue(){N=expiry.ToString()});
        item.Add("status", new AttributeValue(DataRecord.DataRecordStatus.INPROGRESS.ToString()));
        await client.PutItemAsync(new PutItemRequest
        {
            TableName = TABLE_NAME,
            Item = item
        });
        var scanResponse = await client.ScanAsync(new ScanRequest
        {
            TableName = TABLE_NAME
        });
        scanResponse.Items.Count.Should().Be(1);

        // WHEN
        await _dynamoDbPersistenceStore.DeleteRecord("key");

        // THEN
        scanResponse = await client.ScanAsync(new ScanRequest
        {
            TableName = TABLE_NAME
        });
        scanResponse.Items.Count.Should().Be(0);
    }

    [Fact]
    public async Task EndToEndWithCustomAttrNamesAndSortKey()
    {
        var TABLE_NAME_CUSTOM = "idempotency_table_custom";
        try
        {
            var createTableRequest = new CreateTableRequest
            {
                TableName = TABLE_NAME_CUSTOM,
                KeySchema = new List<KeySchemaElement>()
                {
                    new KeySchemaElement("key", KeyType.HASH),
                    new KeySchemaElement("sortkey", KeyType.RANGE)
                },
                AttributeDefinitions = new List<AttributeDefinition>()
                {
                    new AttributeDefinition("key", ScalarAttributeType.S),
                    new AttributeDefinition("sortkey", ScalarAttributeType.S)
                },
                BillingMode = BillingMode.PAY_PER_REQUEST
            };
            await client.CreateTableAsync(createTableRequest);
            DynamoDBPersistenceStore persistenceStore = DynamoDBPersistenceStore.Builder()
                .WithTableName(TABLE_NAME_CUSTOM)
                .WithDynamoDBClient(client)
                .WithDataAttr("result")
                .WithExpiryAttr("expiry")
                .WithKeyAttr("key")
                .WithSortKeyAttr("sortkey")
                .WithStaticPkValue("pk")
                .WithStatusAttr("state")
                .WithValidationAttr("valid")
                .Build();
            persistenceStore.Configure(IdempotencyConfig.Builder().Build(),functionName: null);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            DataRecord record = new DataRecord(
                "mykey",
                DataRecord.DataRecordStatus.INPROGRESS,
                now.AddSeconds(400).ToUnixTimeMilliseconds(),
                null,
                null
            );
            // PUT
            await persistenceStore.PutRecord(record, now);

            Dictionary<String, AttributeValue> customKey = new();
            customKey.Add("key", new AttributeValue("pk"));
            customKey.Add("sortkey", new AttributeValue("mykey"));

            Dictionary<String, AttributeValue> itemInDb = (await client.GetItemAsync(new GetItemRequest
            {
                TableName = TABLE_NAME_CUSTOM,
                Key = customKey
            })).Item;

            // GET
            DataRecord recordInDb = await persistenceStore.GetRecord("mykey");

            itemInDb.Should().NotBeNull();
            itemInDb["key"].S.Should().Be("pk");
            itemInDb["sortkey"].S.Should().Be(recordInDb.IdempotencyKey);
            itemInDb["state"].S.Should().Be(recordInDb.Status.ToString());
            itemInDb["expiry"].N.Should().Be(recordInDb.ExpiryTimestamp.ToString());

            // UPDATE
            DataRecord updatedRecord = new DataRecord(
                "mykey",
                DataRecord.DataRecordStatus.COMPLETED,
                now.AddSeconds(500).ToUnixTimeMilliseconds(),
                "response",
                null
            );
            await persistenceStore.UpdateRecord(updatedRecord);
            recordInDb = await persistenceStore.GetRecord("mykey");
            recordInDb.Should().Be(updatedRecord);

            // DELETE
            await persistenceStore.DeleteRecord("mykey");
            (await client.ScanAsync(new ScanRequest
            {
                TableName = TABLE_NAME_CUSTOM
            })).Count.Should().Be(0);

        }
        finally
        {
            try
            {
                await client.DeleteTableAsync(new DeleteTableRequest
                {
                    TableName = TABLE_NAME_CUSTOM
                });
            }
            catch (Exception)
            {
                // OK
            }
        }
    }

    [Fact]
    public async Task IdempotencyDisabled_NoClientShouldBeCreated() 
    {
        try
        {
            Environment.SetEnvironmentVariable(Constants.IDEMPOTENCY_DISABLED_ENV, "true");

            DynamoDBPersistenceStore store = DynamoDBPersistenceStore.Builder().WithTableName(TABLE_NAME).Build();
            Func<Task> act = () => store.GetRecord("fake");
            await act.Should().ThrowAsync<NullReferenceException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Constants.IDEMPOTENCY_DISABLED_ENV, "false");
        }
    }
    private static Dictionary<string, AttributeValue> CreateKey(string keyValue)
    {
        var key = new Dictionary<string, AttributeValue>()
        {
            {"id", new AttributeValue(keyValue)}
        };
        return key;
    }
}