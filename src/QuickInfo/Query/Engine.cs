﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using static QuickInfo.HtmlFactory;

namespace QuickInfo
{
    public class Engine
    {
        private List<IProcessor> processors = new List<IProcessor>();
        private List<IStructureParser> structureParsers = new List<IStructureParser>();

        private Engine()
        {
            var assembly = typeof(Engine).GetTypeInfo().Assembly;
            var processorTypes = assembly.GetTypes()
                .Where(t => t.GetTypeInfo().ImplementedInterfaces.Contains(typeof(IProcessor)));

            processors.AddRange(processorTypes.Select(t => (IProcessor)Activator.CreateInstance(t)).OrderBy(p => p.GetType().Name));

            structureParsers.Add(new UnitParser());
            structureParsers.Add(new Keyword("rgb"));
            structureParsers.Add(new Keyword("in"));
            structureParsers.Add(new Keyword("to"));
            structureParsers.Add(new Keyword("hex"));
            structureParsers.Add(new Invocation());
            structureParsers.Add(new Prefix("#"));
            structureParsers.Add(new Prefix("U+", "u+", "\\U", "\\u"));
            structureParsers.Add(new Prefix("utf8 ", "utf-8 ", "utf "));
            structureParsers.Add(new Prefix("unicode ", "char ", "emoji "));
            structureParsers.Add(new Integer());
            structureParsers.Add(new Double());
            structureParsers.Add(new SeparatedList(','));
            structureParsers.Add(new SeparatedList(' '));
        }

        public static Engine Instance { get; } = new Engine();

        private static readonly char[] multipleQuerySeparator = new[] { '|' };

        public static string GetResponse(string input, HttpRequest request = null)
        {
            if (string.IsNullOrEmpty(input))
            {
                return Div("");
            }

            if (input.IndexOf('|') != -1)
            {
                var sb = new StringBuilder();
                sb.AppendLine("<div class=\"answersList\">");
                var multipleQueries = input.Split(multipleQuerySeparator, StringSplitOptions.RemoveEmptyEntries);
                foreach (var singleQuery in multipleQueries)
                {
                    var result = Instance.GetSingleResponseWorker(singleQuery, request);
                    if (string.IsNullOrEmpty(result))
                    {
                        result = DivClass("No results.", "note");
                    }

                    sb.AppendLine("<div class=\"answerBlock\">");
                    sb.AppendLine(DivClass(singleQuery, "answerBlockHeader"));

                    if (!result.Contains("singleAnswerSection"))
                    {
                        result = DivClass(result, "singleAnswerSection");
                    }
                    else
                    {
                        result = Div(result);
                    }

                    sb.AppendLine(result);

                    sb.AppendLine("</div>");
                }

                if (multipleQueries.Length == 0)
                {
                    sb.AppendLine(DivClass("No results.", "note"));
                }

                sb.AppendLine("</div>");

                return sb.ToString();
            }
            else
            {
                var result = Instance.GetSingleResponseWorker(input, request);
                if (string.IsNullOrEmpty(result))
                {
                    result = DivClass($"No results. {SearchLink("Enter ? for help.", "?")}", "note");
                }

                result = DivClass(result, "answersList");

                return result;
            }
        }

        public static object Parse(string input)
        {
            return Instance.ParseWorker(input);
        }

        public static T TryGetStructure<T>(object instance)
        {
            if (instance == null)
            {
                return default(T);
            }

            if (instance is T)
            {
                return (T)instance;
            }

            if (typeof(T) == typeof(byte[]))
            {
                var separatedList = TryGetStructure<SeparatedList>(instance);
                if (separatedList != null)
                {
                    var byteList = separatedList.GetStructuresOfType<Integer>();
                    if (byteList.Count == separatedList.Count && byteList.All(b => b.Value >= 0 && b.Value <= 255))
                    {
                        List<byte> result = new List<byte>();
                        foreach (var b in byteList)
                        {
                            result.Add((byte)b.ForceHexadecimalValue());
                        }

                        return (T)(object)result.ToArray();
                    }
                }
            }

            IEnumerable<object> list = instance as IEnumerable<object>;
            if (list != null)
            {
                foreach (var item in list)
                {
                    var structure = TryGetStructure<T>(item);
                    if (structure != null)
                    {
                        return structure;
                    }
                }
            }

            var integer = instance as Integer;
            if (integer != null && typeof(T) == typeof(Double))
            {
                return (T)(object)new Double(integer.Int32);
            }

            return default(T);
        }

        private object ParseWorker(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return null;
            }

            input = input.Trim();

            var list = new List<object>();

            foreach (var parser in structureParsers)
            {
                var result = parser.TryParse(input);
                if (result != null)
                {
                    if (result is Double d && list.Count == 1 && list[0] is Integer integer && ((double)integer.Value) == d.Value)
                    {
                        // skip the double if the int is the same
                    }
                    else
                    {
                        list.Add(result);
                    }
                }
            }

            if (list.Count == 0)
            {
                return null;
            }
            else if (list.Count == 1)
            {
                return list[0];
            }
            else
            {
                return list;
            }
        }

        public IEnumerable<IProcessor> Processors
        {
            get { return this.processors; }
        }

        private string GetSingleResponseWorker(string input, HttpRequest request = null)
        {
            var query = new Query(input);
            query.Request = request;

            List<(string processorName, string resultText)> results = new List<(string, string)>();
            foreach (var processor in processors)
            {
                var result = processor.GetResult(query);
                if (!string.IsNullOrEmpty(result))
                {
                    results.Add((processor.GetType().Name, result));
                }
            }

            if (results.Count == 0)
            {
                return null;
            }

            if (results.Count == 1)
            {
                return results[0].resultText;
            }

            var sb = new StringBuilder();
            foreach (var result in results)
            {
                var toAppend = result.resultText;
                if (!toAppend.Contains("answerSection"))
                {
                    if (query.IsHelp)
                    {
                        toAppend = DivClass(toAppend, "singleAnswerSection");
                    }
                    else
                    {
                        toAppend = DivClass(toAppend, "answerSection");
                    }
                }

                if (query.IsHelp)
                {
                    toAppend = DivClass(result.processorName, "answerBlockHeader") + toAppend;

                    toAppend = DivClass(
                        toAppend,
                        "answerBlock");
                }

                sb.AppendLine(toAppend);
            }

            var response = sb.ToString();

            if (query.IsHelp)
            {
                response = DivClass(response, "singleAnswerSection");
            }

            return response;
        }
    }
}
