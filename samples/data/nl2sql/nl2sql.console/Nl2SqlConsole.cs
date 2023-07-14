﻿// Copyright (c) Microsoft. All rights reserved.
namespace SemanticKernel.Data.Nl2Sql;

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using SemanticKernel.Data.Nl2Sql.Query;
using SemanticKernel.Data.Nl2Sql.Services;

internal class Nl2SqlConsole : BackgroundService
{
    private const ConsoleColor FocusColor = ConsoleColor.Yellow;
    private const ConsoleColor PromptColor = ConsoleColor.Gray;
    private const ConsoleColor QueryColor = ConsoleColor.Green;
    private const ConsoleColor SystemColor = ConsoleColor.Cyan;

    private static Dictionary<Type, int> typeWidths =
        new()
        {
            {typeof(bool), 5 },
            {typeof(int), 9 },
            {typeof(DateTime), 12 },
            {typeof(TimeSpan), 12 },
            {typeof(Guid), 8 },
        };

    private readonly IKernel kernel;
    private readonly SqlConnectionProvider sqlProvider;
    private readonly SchemaProvider schemaProvider;
    private readonly SqlQueryGenerator queryGenerator;
    private readonly ILogger<Nl2SqlConsole> logger;

    public Nl2SqlConsole(
        IKernel kernel,
        SqlConnectionProvider sqlProvider,
        SchemaProvider schemaProvider,
        ILogger<Nl2SqlConsole> logger)
    {
        this.kernel = kernel;
        this.sqlProvider = sqlProvider;
        this.schemaProvider = schemaProvider;
        this.logger = logger;
        this.queryGenerator = new SqlQueryGenerator(this.kernel, schemaProvider);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // Yield to hosting framework prior to blocking console (for input)

        foreach ((var name, var schema) in this.schemaProvider.Schemas) // $$$ HACK
        {
            var schemaText = await this.schemaProvider.GetSchemaAsync(name).ConfigureAwait(false);
            await this.kernel.Memory.SaveInformationAsync("schemas", schemaText, schema.Name, null, null, stoppingToken).ConfigureAwait(false);
        }

        this.WriteIntroduction();

        while (!stoppingToken.IsCancellationRequested)
        {
            var objective = await ReadInputAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(objective))
            {
                continue;
            }

            this.logger.LogInformation($"Objective: {objective}"); // $$$ KERNEL LOGGING ???

            var recall = await this.kernel.Memory.SearchAsync("schemas", objective, limit: 1, minRelevanceScore: 0.75, withEmbeddings: true, stoppingToken).ToArrayAsync().ConfigureAwait(false); // $$$ HACK

            if (recall.Length > 0)
            {

            }

            var schemaName = recall.FirstOrDefault()?.Metadata.Id;
            var schemaText = recall.FirstOrDefault()?.Metadata.Text;

            var query =
                await this.queryGenerator.SolveObjectiveAsync(
                    objective,
                    schemaText,
                    this.kernel.CreateNewContext()).ConfigureAwait(false);

            await ProcessQueryAsync(schemaName, query).ConfigureAwait(false);
        }

        this.WriteLine();

        // Capture console input with cancellation detection
        async Task<string?> ReadInputAsync()
        {
            this.Write(SystemColor, "# ");

            var inputTask = Console.In.ReadLineAsync(stoppingToken).AsTask();
            var objective = await inputTask.ConfigureAwait(false);

            // Null response occurs when blocking input is cancelled (CTRL+C)
            if (null == objective)
            {
                this.WriteLine();
                this.WriteLine(FocusColor, "Cancellation detected...");

                // Yield to sync stoppingToken state
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            else if (string.IsNullOrWhiteSpace(objective))
            {
                this.WriteLine(FocusColor, $"Please provide a query related to the defined schemas.{Environment.NewLine}");
            }
            else
            {
                this.ClearLine(previous: true);
                this.WriteLine(QueryColor, $"# {objective}");
            }

            return objective;
        }

        async Task ProcessQueryAsync(string? schema, string? query)
        {
            if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(query))
            {
                this.WriteLine(FocusColor, $"Unable to translate request into a query.{Environment.NewLine}");
                return;
            }

            this.WriteLine(SystemColor, $"{Environment.NewLine}SCHEMA:");
            this.WriteLine(QueryColor, schema);
            this.WriteLine(SystemColor, $"{Environment.NewLine}QUERY:");
            this.WriteLine(QueryColor, query);

            if (!this.Confirm("Execute?"))
            {
                this.WriteLine();
                this.WriteLine();
                return;
            }

            await Task.Delay(300).ConfigureAwait(false); // Human feedback window

            this.ClearLine();
            this.Write(SystemColor, "Executing...");

            await ProcessDataAsync(
                schema,
                query,
                reader =>
                {
                    this.ClearLine();
                    this.WriteData(reader);
                }).ConfigureAwait(false);

        }

        async Task ProcessDataAsync(string schema, string query, Action<IDataReader> callback)
        {
            try
            {
                using var connection = await this.sqlProvider.ConnectAsync(schema).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = query;

                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                callback.Invoke(reader);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception exception)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                this.ClearLine();
                this.WriteLine(FocusColor, exception.Message);
            }
        }
    }

    private bool Confirm(string message)
    {
        this.Write(FocusColor, $"{Environment.NewLine}{message} (y/n) ");

        while (true)
        {
            var choice = Console.ReadKey(intercept: true);
            switch (char.ToUpperInvariant(choice.KeyChar))
            {
                case 'N':
                    this.Write(FocusColor, "N");
                    return false;
                case 'Y':
                    this.Write(FocusColor, "Y");
                    return true;
                default:
                    break;
            }
        }
    }

    private void WriteData(IDataReader reader)
    {
        int maxPage = Console.WindowHeight - 10;

        var widths = GetWidths().ToArray();
        var isColumnTruncation = widths.Length < reader.FieldCount;
        var rowFormatter = string.Join('│', widths.Select((width, index) => width == -1 ? $"{{{index}}}" : $"{{{index},-{width}}}"));

        if (isColumnTruncation)
        {
            rowFormatter = string.Concat(rowFormatter, isColumnTruncation ? $"│{{{widths.Length}}}" : string.Empty);
        }

        WriteRow(GetColumns());

        WriteSeparator(widths);

        bool showData;

        do
        {
            int count = 0;
            while (reader.Read() && count < maxPage)
            {
                WriteRow(GetValues());

                count++;
            }

            if (count >= maxPage)
            {
                showData = this.Confirm($"...More?");
                this.ClearLine();
            }
            else
            {
                showData = false;
                this.WriteLine();
            }
        }
        while (showData);

        void WriteRow(IEnumerable<string> fields)
        {
            fields = TrimValues(fields).Concat(isColumnTruncation ? new[] { "..." } : Array.Empty<string>());

            this.WriteLine(SystemColor, rowFormatter, fields.ToArray());

        }

        IEnumerable<string> TrimValues(IEnumerable<string> fields)
        {
            int index = 0;

            foreach (var field in fields)
            {
                if (index >= widths.Length)
                {
                    yield break;
                }

                var width = widths[index];
                ++index;

                if (width == -1 || field.Length <= width)
                {
                    yield return field;
                    continue;
                }

                yield return field?.Substring(0, width - 4) + "...";
            }
        }

        void WriteSeparator(int[] widths)
        {
            for (int index = 0; index < widths.Length; index++)
            {
                if (index > 0)
                {
                    this.Write(SystemColor, "┼");
                }

                var width = widths[index];

                this.Write(SystemColor, new string('─', width == -1 ? 24 : width));
            }

            if (isColumnTruncation)
            {
                this.Write(SystemColor, "┼───");
            }

            this.WriteLine();
        }

        IEnumerable<int> GetWidths()
        {
            if (reader.FieldCount == 1)
            {
                yield return -1;
                yield break;
            }

            int totalWidth = 0;

            for (int index = 0; index < reader.FieldCount; ++index)
            {
                var width = GetWidth(reader.GetFieldType(index));

                if (totalWidth + width > Console.WindowWidth - 11)
                {
                    yield break;
                }

                totalWidth += width;

                yield return width;
            }
        }

        static int GetWidth(Type type)
        {
            if (!typeWidths.TryGetValue(type, out var width))
            {
                return 16;
            }

            return width;
        }

        IEnumerable<string> GetColumns()
        {
            for (int index = 0; index < reader.FieldCount; ++index)
            {
                var label = reader.GetName(index);

                yield return string.IsNullOrWhiteSpace(label) ? $"#{index + 1}" : label;
            }
        }

        IEnumerable<string> GetValues()
        {
            for (int index = 0; index < reader.FieldCount; ++index)
            {
                yield return reader.GetValue(index)?.ToString() ?? string.Empty;
            }
        }
    }

    private void WriteIntroduction()
    {
        this.WriteLine(SystemColor, $"I can translate your question into a SQL query for the following data schemas:{Environment.NewLine}");

        foreach ((var name, var schema) in this.schemaProvider.Schemas)
        {
            this.WriteLine(SystemColor, $"- {name}");
        }

        this.WriteLine(SystemColor, $"{Environment.NewLine}Press CTRL+C to Exit.{Environment.NewLine}");
    }

    private void WriteLine(ConsoleColor? color = null, string? message = null, params string[] args)
    {
        this.Write(color ?? Console.ForegroundColor, message ?? string.Empty, args);

        Console.WriteLine();
    }

    private void Write(ConsoleColor color, string message, params string[] args)
    {
        var currentColor = Console.ForegroundColor;

        try
        {
            Console.ForegroundColor = color;
            Console.Write(message, args);
        }
        finally
        {
            Console.ForegroundColor = currentColor;
        }
    }

    private void ClearLine(bool previous = false)
    {
        if (previous)
        {
            --Console.CursorTop;
        }

        Console.CursorLeft = 0;
        Console.Write(new string(' ', Console.WindowWidth));
        Console.CursorLeft = 0;
    }
}
