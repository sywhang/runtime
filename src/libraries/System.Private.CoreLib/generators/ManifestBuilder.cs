// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Generators
{
    public partial class EventSourceGenerator
    {
        /// <summary>
        /// ManifestBuilder is designed to isolate the details of the message of the event from the
        /// rest of EventSource.  This one happens to create XML.
        /// </summary>
        public class ManifestBuilder
        {
            //private const string dllName = "System.Private.CoreLib";
            private StringBuilder _builder;

            /// <summary>
            /// Build a manifest for 'providerName' with the given GUID, which will be packaged into 'dllName'.
            /// 'resources, is a resource manager.  If specified all messages are localized using that manager.
            /// </summary>
            public ManifestBuilder(StringBuilder builder, string providerName, Guid providerGuid, Dictionary<ulong, string>? keywordMap, Dictionary<int, string>? taskMap)
            {
                this.providerName = providerName;
                this._builder = builder;
                sb = new StringBuilder();
                events = new StringBuilder();
                templates = new StringBuilder();
                opcodeTab = new Dictionary<int, string>();
                stringTab = new Dictionary<string, string>();
                errors = new List<string>();
                perEventByteArrayArgIndices = new Dictionary<string, List<int>>();

                sb.AppendLine("<instrumentationManifest xmlns=\"http://schemas.microsoft.com/win/2004/08/events\">");
                sb.AppendLine(" <instrumentation xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:win=\"http://manifests.microsoft.com/win/2004/08/windows/events\">");
                sb.AppendLine("  <events xmlns=\"http://schemas.microsoft.com/win/2004/08/events\">");
                sb.Append("<provider name=\"").Append(providerName).
                Append("\" guid=\"{").Append(providerGuid.ToString()).Append("}");
                string symbolsName = providerName.Replace("-", "").Replace('.', '_');  // Period and - are illegal replace them.
                sb.Append("\" symbol=\"").Append(symbolsName);
                sb.AppendLine("\">");

                keywordTab = keywordMap;
                taskTab = taskMap;

                // TODO: Remove these once we figure out a long-term localization replacement solution
                stringTab.Add("event_TaskCompleted", "Task {2} completed.");
                stringTab.Add("event_TaskScheduled", "Task {2} scheduled to TaskScheduler {0}.");
                stringTab.Add("event_TaskStarted", "Task {2} executing.");
                stringTab.Add("event_TaskWaitBegin", "Beginning wait ({3}) on Task {2}.");
                stringTab.Add("event_TaskWaitEnd", "Ending wait on Task {2}.");
            }

            public void AddOpcode(string name, int value)
            {
                opcodeTab[value] = name;
            }

            public void AddTask(string name, int value)
            {
                taskTab ??= new Dictionary<int, string>();
                if (taskTab.ContainsValue(name))
                {
                    return;
                }

                taskTab[value] = name;
                stringTab.Add($"keyword_{name}", name);
            }

            public void AddKeyword(string name, ulong value)
            {
                if ((value & (value - 1)) != 0)   // Is it a power of 2?
                {
                    return;
                }
                keywordTab ??= new Dictionary<ulong, string>();
                keywordTab[value] = name;
            }
            public void AddMap(Dictionary<string, Dictionary<string, int>> ecMap)
            {
                maps ??= new Dictionary<string, Dictionary<string, int>>();

                foreach (string enumName in ecMap.Keys)
                {
                    if (!maps.ContainsKey(enumName))
                    {
                        maps.Add(enumName, new Dictionary<string, int>());
                        
                        foreach (string fieldName in ecMap[enumName].Keys)
                        {
                            if (!maps[enumName].ContainsKey(fieldName))
                            {
                                maps[enumName][fieldName] = ecMap[enumName][fieldName];
                            }
                        }
                    }
                }
            }

            public void StartEvent(string eventName, EventAttribute eventAttribute)
            {
                this.eventName = eventName;
                this.numParams = 0;
                byteArrArgIndices = null;
                string taskName = eventName;

                // TODO: Add additional logic here for Start/Stop events
                if (eventAttribute.Task == EventTask.None)
                {
                    eventAttribute.Task = (EventTask)(0xFFFE - eventAttribute.EventId);
                }
                events.Append("  <event value=\"").Append(eventAttribute.EventId).
                    Append("\" version=\"").Append(eventAttribute.Version).
                    Append("\" level=\"");
                AppendLevelName(events, eventAttribute.Level);
                events.Append("\" symbol=\"").Append(eventName).Append('"');
                // at this point we add to the manifest's stringTab a message that is as-of-yet
                // "untranslated to manifest convention", b/c we don't have the number or position
                // of any byte[] args (which require string format index updates)
                WriteMessageAttrib(events, "event", eventName, eventAttribute.Message);

                if (eventAttribute.Keywords != 0)
                {
                    events.Append(" keywords=\"");
                    AppendKeywords(events, (ulong)eventAttribute.Keywords, eventName);
                    events.Append('"');
                }

                if (eventAttribute.Task != 0)
                {
                    events.Append(" task=\"").Append(taskName).Append('"');

                    AddTask(taskName, (int)eventAttribute.Task);
                }
            }

            public void AddEventParameter(ITypeSymbol? type, string name)
            {
                if (type is null)
                {
                    // ???
                    return;
                }

                if (this.numParams == 0)
                    templates.Append("  <template tid=\"").Append(this.eventName).AppendLine("Args\">");
                if (type.ToDisplayString() == "byte[]")
                {
                    // mark this index as "extraneous" (it has no parallel in the managed signature)
                    // we use these values in TranslateToManifestConvention()
                    byteArrArgIndices ??= new List<int>(4);
                    byteArrArgIndices.Add(numParams);

                    // add an extra field to the template representing the length of the binary blob
                    numParams++;
                    templates.Append("   <data name=\"").Append(name).AppendLine("Size\" inType=\"win:UInt32\"/>");
                }
                numParams++;
                templates.Append("   <data name=\"").Append(name).Append("\" inType=\"").Append(GetTypeName(type)).Append('"');

                if ((type.TypeKind == TypeKind.Array && ((IArrayTypeSymbol)type).ElementType.SpecialType == SpecialType.System_Byte) ||
                    (type.TypeKind == TypeKind.Pointer && ((IPointerTypeSymbol)type).PointedAtType.SpecialType == SpecialType.System_Byte))
                {
                    // add "length" attribute to the "blob" field in the template (referencing the field added above)
                    templates.Append(" length=\"").Append(name).Append("Size\"");
                }

                // ETW does not support 64-bit value maps, so we don't specify these as ETW maps
                if (type.TypeKind == TypeKind.Enum)
                {
                    INamedTypeSymbol? underlyingEnumType = ((INamedTypeSymbol)type).EnumUnderlyingType;
                    if (underlyingEnumType is not null)
                    {
                        if (underlyingEnumType.SpecialType != SpecialType.System_Int64)
                        {
                            templates.Append(" map=\"").Append(type.Name).Append('"');
                        }
                    }
                }
                templates.AppendLine("/>");
            }
            private void WriteMessageAttrib(StringBuilder stringBuilder, string elementName, string name, string? value)
            {
                string? key = null;
                if (value == null)
                    return;

                key ??= elementName + "_" + name;
                stringBuilder.Append(" message=\"$(string.").Append(key).Append(")\"");

                if (stringTab.TryGetValue(key, out string? prevValue) && !prevValue.Equals(value))
                {
                    return;
                }

                stringTab[key] = value;
            }

            private static void AppendLevelName(StringBuilder sb, EventLevel level)
            {
                if ((int)level < 16)
                {
                    sb.Append("win:");
                }

                sb.Append(level switch 
                {
                    EventLevel.LogAlways => nameof(EventLevel.LogAlways),
                    EventLevel.Critical => nameof(EventLevel.Critical),
                    EventLevel.Error => nameof(EventLevel.Error),
                    EventLevel.Warning => nameof(EventLevel.Warning),
                    EventLevel.Informational => nameof(EventLevel.Informational),
                    EventLevel.Verbose => nameof(EventLevel.Verbose),
                    _ => ((int)level).ToString()
                });
            }

            public void EndEvent(string eventName)
            {
                if (numParams > 0)
                {
                    templates.AppendLine("  </template>");
                    events.Append(" template=\"").Append(eventName).Append("Args\"");
                }
                events.AppendLine("/>");

                if (byteArrArgIndices != null)
                    perEventByteArrayArgIndices[eventName] = byteArrArgIndices;

                // at this point we have all the information we need to translate the C# Message
                // to the manifest string we'll put in the stringTab
                string prefixedEventName = "event_" + eventName;
                if (stringTab.TryGetValue(prefixedEventName, out string? msg))
                {
                    msg = TranslateToManifestConvention(msg, eventName);
                    stringTab[prefixedEventName] = msg;
                }
            }

            private string TranslateToManifestConvention(string eventMessage, string evtName)
            {
                StringBuilder? stringBuilder = null;        // We lazily create this
                int writtenSoFar = 0;
                for (int i = 0; ;)
                {
                    if (i >= eventMessage.Length)
                    {
                        if (stringBuilder is null)
                            return eventMessage;
                        UpdateStringBuilder(ref stringBuilder, eventMessage, writtenSoFar, i - writtenSoFar);
                        return stringBuilder!.ToString();
                    }

                    int chIdx;
                    if (eventMessage[i] == '%')
                    {
                        // handle format message escaping character '%' by escaping it
                        UpdateStringBuilder(ref stringBuilder, eventMessage, writtenSoFar, i - writtenSoFar);
                        stringBuilder!.Append("%%");
                        i++;
                        writtenSoFar = i;
                    }
                    else if (i < eventMessage.Length - 1 &&
                        (eventMessage[i] == '{' && eventMessage[i + 1] == '{' || eventMessage[i] == '}' && eventMessage[i + 1] == '}'))
                    {
                        // handle C# escaped '{" and '}'
                        UpdateStringBuilder(ref stringBuilder, eventMessage, writtenSoFar, i - writtenSoFar);
                        stringBuilder!.Append(eventMessage[i]);
                        i++; i++;
                        writtenSoFar = i;
                    }
                    else if (eventMessage[i] == '{')
                    {
                        int leftBracket = i;
                        i++;
                        int argNum = 0;
                        while (i < eventMessage.Length && char.IsDigit(eventMessage[i]))
                        {
                            argNum = argNum * 10 + eventMessage[i] - '0';
                            i++;
                        }
                        if (i < eventMessage.Length && eventMessage[i] == '}')
                        {
                            i++;
                            UpdateStringBuilder(ref stringBuilder, eventMessage, writtenSoFar, leftBracket - writtenSoFar);
                            int manIndex = TranslateIndexToManifestConvention(argNum, evtName);
                            stringBuilder!.Append('%').Append(manIndex);
                            // An '!' after the insert specifier {n} will be interpreted as a literal.
                            // We'll escape it so that mc.exe does not attempt to consider it the
                            // beginning of a format string.
                            if (i < eventMessage.Length && eventMessage[i] == '!')
                            {
                                i++;
                                stringBuilder.Append("%!");
                            }
                            writtenSoFar = i;
                        }
                    }
                    else if ((chIdx = "&<>'\"\r\n\t".IndexOf(eventMessage[i])) >= 0)
                    {
                        UpdateStringBuilder(ref stringBuilder, eventMessage, writtenSoFar, i - writtenSoFar);
                        i++;
                        stringBuilder!.Append(s_escapes[chIdx]);
                        writtenSoFar = i;
                    }
                    else
                        i++;
                }
            }
            private int TranslateIndexToManifestConvention(int idx, string evtName)
            {
                if (perEventByteArrayArgIndices.TryGetValue(evtName, out List<int>? byteArrArgIndices))
                {
                    foreach (int byArrIdx in byteArrArgIndices)
                    {
                        if (idx >= byArrIdx)
                            ++idx;
                        else
                            break;
                    }
                }
                return idx + 1;
            }


            private static void UpdateStringBuilder(ref StringBuilder? stringBuilder, string eventMessage, int startIndex, int count)
            {
                stringBuilder ??= new StringBuilder();
                stringBuilder.Append(eventMessage, startIndex, count);
            }

            public string CreateManifestString()
            {
                Span<char> ulongHexScratch = stackalloc char[16]; // long enough for ulong.MaxValue formatted as hex
                // Write out the tasks
                if (taskTab != null)
                {
                    sb.AppendLine(" <tasks>");
                    var sortedTasks = new List<int>(taskTab.Keys);
                    sortedTasks.Sort();
                    foreach (int task in sortedTasks)
                    {
                        sb.Append("  <task");
                        WriteNameAndMessageAttribs(sb, "task", taskTab[task]);
                        sb.Append(" value=\"").Append(task).AppendLine("\"/>");
                    }
                    sb.AppendLine(" </tasks>");
                }

                // Write out the maps
                sb.AppendLine(" <maps>");
                foreach (string enumName in maps.Keys)
                {
                    sb.Append("  <").Append("valueMap").Append(" name=\"").Append(enumName).AppendLine("\">");
                    foreach (string fieldName in maps[enumName].Keys)
                    {
                        ulong hexValue = (ulong)Convert.ToInt64(maps[enumName][fieldName]);
                        string hexValueFormatted = hexValue.ToString("x", CultureInfo.InvariantCulture);
                        sb.Append("   <").Append("map value=\"0x").Append(hexValueFormatted).Append('"');
                        WriteMessageAttrib(sb, "map", enumName + "." + fieldName, fieldName);
                        sb.AppendLine("/>");
                    }
                    sb.AppendLine("  </valueMap>");
                }
                sb.AppendLine(" </maps>");

                // Write out the keywords
                if (keywordTab != null)
                {
                    sb.AppendLine(" <keywords>");
                    var sortedKeywords = new List<ulong>(keywordTab.Keys);
                    sortedKeywords.Sort();
                    foreach (ulong keyword in sortedKeywords)
                    {
                        sb.Append("  <keyword");
                        WriteNameAndMessageAttribs(sb, "keyword", keywordTab[keyword]);
                        string hexValueFormatted = keyword.ToString("x", CultureInfo.InvariantCulture);
                        sb.Append(" mask=\"0x").Append(hexValueFormatted).AppendLine("\"/>");
                    }
                    sb.AppendLine(" </keywords>");
                }


                sb.AppendLine(" <events>");
                sb.Append(events.ToString());
                sb.AppendLine(" </events>");
                sb.AppendLine(" <templates>");
                sb.Append(templates.ToString());
                sb.AppendLine(" </templates>");
                sb.AppendLine("</provider>");
                sb.AppendLine("</events>");
                sb.AppendLine("</instrumentation>");

                sb.AppendLine("<localization>");

                var sortedStrings = new string[stringTab.Keys.Count];
                stringTab.Keys.CopyTo(sortedStrings, 0);
                Array.Sort<string>(sortedStrings, 0, sortedStrings.Length);

                CultureInfo ci = CultureInfo.CurrentUICulture;
                sb.Append(" <resources culture=\"").Append(ci.Name).AppendLine("\">");
                sb.AppendLine("  <stringTable>");
                foreach (string stringKey in sortedStrings)
                {
                    stringTab.TryGetValue(stringKey, out string val);
                    if (val != null)
                    {
                        sb.Append("   <string id=\"").Append(stringKey).Append("\" value=\"").Append(val).AppendLine("\"/>");
                    }
                }
                sb.AppendLine("  </stringTable>");
                sb.AppendLine(" </resources>");

                sb.AppendLine("</localization>");
                sb.AppendLine("</instrumentationManifest>");

                return sb.ToString();
            }
            private void WriteNameAndMessageAttribs(StringBuilder stringBuilder, string elementName, string name)
            {
                stringBuilder.Append(" name=\"").Append(name).Append('"');
                WriteMessageAttrib(sb, elementName, name, name);
            }
            private void AppendKeywords(StringBuilder sb, ulong keywords, string eventName)
            {
                // ignore keywords associate with channels
                // See ValidPredefinedChannelKeywords def for more.
                keywords &= ~ValidPredefinedChannelKeywords;
                bool appended = false;
                for (ulong bit = 1; bit != 0; bit <<= 1)
                {
                    if ((keywords & bit) != 0)
                    {
                        string? keyword = null;
                        if ((keywordTab == null || !keywordTab.TryGetValue(bit, out keyword)) &&
                            (bit >= (ulong)0x1000000000000))
                        {
                            // do not report Windows reserved keywords in the manifest (this allows the code
                            // to be resilient to potential renaming of these keywords)
                            keyword = string.Empty;
                        }
                        if (keyword == null)
                        {
                            keyword = string.Empty;
                        }

                        if (keyword.Length != 0)
                        {
                            if (appended)
                            {
                                sb.Append(' ');
                            }

                            sb.Append(keyword);
                            appended = true;
                        }
                    }
                }
            }
            private string? GetLocalizedMessage(string key, CultureInfo ci, bool etwFormat)
            {
                string? value = null;
                if (etwFormat && value == null)
                    stringTab.TryGetValue(key, out value);

                return value;
            }


            private string GetTypeName(ITypeSymbol type)
            {
                if (type is null) return string.Empty;
                if (type.TypeKind == TypeKind.Enum)
                {
                    ITypeSymbol? enumType = ((INamedTypeSymbol)type).EnumUnderlyingType;
                    if (enumType is not null)
                    {
                        string typeName = GetTypeName(enumType);
                        return typeName.Replace("win:Int", "win:UInt"); // ETW requires enums to be unsigned.
                    }
                    else
                    {
                        // TODO: error handling?
                        return string.Empty;
                    }
                }

                ITypeSymbol underlyingType = type.OriginalDefinition;

                switch (underlyingType.SpecialType)
                {
                    case SpecialType.System_Boolean:
                        return "win:Boolean";
                    case SpecialType.System_Byte:
                        return "win:UInt8";
                    case SpecialType.System_Char:
                    case SpecialType.System_UInt16:
                        return "win:UInt16";
                    case SpecialType.System_UInt32:
                        return "win:UInt32";
                    case SpecialType.System_UInt64:
                        return "win:UInt64";
                    case SpecialType.System_SByte:
                        return "win:Int8";
                    case SpecialType.System_Int16:
                        return "win:Int16";
                    case SpecialType.System_Int32:
                        return "win:Int32";
                    case SpecialType.System_Int64:
                        return "win:Int64";
                    case SpecialType.System_String:
                        return "win:UnicodeString";
                    case SpecialType.System_Single:
                        return "win:Float";
                    case SpecialType.System_Double:
                        return "win:Double";
                    case SpecialType.System_DateTime:
                        return "win:FILETIME";
                    default:
                        if (type.ToDisplayString() == "Guid")
                            return "win:GUID";
                        else if (type.SpecialType == SpecialType.System_IntPtr)
                            return "win:Pointer";
                        else if (type.TypeKind == TypeKind.Array && ((IArrayTypeSymbol)type).ElementType.SpecialType == SpecialType.System_Byte)
                        {
                            return "win:Binary";
                        }
                        else if (type.TypeKind == TypeKind.Pointer &&  ((IPointerTypeSymbol)type).PointedAtType.SpecialType == SpecialType.System_Byte)
                        {
                            return "win:Binary";
                        }

                        // TODO: Error handling
                        return string.Empty;
                }
            }

            private static readonly string[] s_escapes = { "&amp;", "&lt;", "&gt;", "&apos;", "&quot;", "%r", "%n", "%t" };
            // Manifest messages use %N conventions for their message substitutions.   Translate from
            // .NET conventions.   We can't use RegEx for this (we are in mscorlib), so we do it 'by hand'

            private readonly Dictionary<int, string> opcodeTab;
            private Dictionary<int, string>? taskTab;
            private Dictionary<ulong, string>? keywordTab;
            private readonly Dictionary<string, string> stringTab;       // Maps unlocalized strings to localized ones

            // WCF used EventSource to mimic a existing ETW manifest.   To support this
            // in just their case, we allowed them to specify the keywords associated
            // with their channels explicitly.   ValidPredefinedChannelKeywords is
            // this set of channel keywords that we allow to be explicitly set.  You
            // can ignore these bits otherwise.
            internal const ulong ValidPredefinedChannelKeywords = 0xF000000000000000;

            private readonly StringBuilder sb;               // Holds the provider information.
            private readonly StringBuilder events;           // Holds the events.
            private readonly StringBuilder templates;

            private readonly string providerName;
            private readonly IList<string> errors;           // list of currently encountered errors
            private readonly Dictionary<string, List<int>> perEventByteArrayArgIndices;  // "event_name" -> List_of_Indices_of_Byte[]_Arg

            // State we track between StartEvent and EndEvent.
            private string? eventName;               // Name of the event currently being processed.
            private int numParams;                  // keeps track of the number of args the event has.
            private List<int>? byteArrArgIndices;   // keeps track of the index of each byte[] argument

            private Dictionary<string, Dictionary<string, int>> maps;
        }

        /// EventPipeManifestBuilder builds manifest data for EventPipe data
        private class EventPipeManifestBuilder
        {
            public static void BuildManifest(StringBuilder builder, List<EventSourceEvent> events)
            {
                builder.AppendLine("m_eventPipeMetadataGenerator = (int eventID) => {{");
                builder.AppendLine("    switch (eventID)");
                builder.AppendLine("    {");

                foreach (EventSourceEvent evt in events)
                {

                    builder.Append("        case ").Append(evt.Id).AppendLine(":");
                    builder.Append("            return ")

                }
            }

            private static bool GetTypeInfoFromType(Type? type, out TraceLoggingTypeInfo? typeInfo)
            {
                if (type == typeof(bool))
                {
                    typeInfo = ScalarTypeInfo.Boolean();
                    return true;
                }
                else if (type == typeof(byte))
                {
                    typeInfo = ScalarTypeInfo.Byte();
                    return true;
                }
                else if (type == typeof(sbyte))
                {
                    typeInfo = ScalarTypeInfo.SByte();
                    return true;
                }
                else if (type == typeof(char))
                {
                    typeInfo = ScalarTypeInfo.Char();
                    return true;
                }
                else if (type == typeof(short))
                {
                    typeInfo = ScalarTypeInfo.Int16();
                    return true;
                }
                else if (type == typeof(ushort))
                {
                    typeInfo = ScalarTypeInfo.UInt16();
                    return true;
                }
                else if (type == typeof(int))
                {
                    typeInfo = ScalarTypeInfo.Int32();
                    return true;
                }
                else if (type == typeof(uint))
                {
                    typeInfo = ScalarTypeInfo.UInt32();
                    return true;
                }
                else if (type == typeof(long))
                {
                    typeInfo = ScalarTypeInfo.Int64();
                    return true;
                }
                else if (type == typeof(ulong))
                {
                    typeInfo = ScalarTypeInfo.UInt64();
                    return true;
                }
                else if (type == typeof(IntPtr))
                {
                    typeInfo = ScalarTypeInfo.IntPtr();
                    return true;
                }
                else if (type == typeof(UIntPtr))
                {
                    typeInfo = ScalarTypeInfo.UIntPtr();
                    return true;
                }
                else if (type == typeof(float))
                {
                    typeInfo = ScalarTypeInfo.Single();
                    return true;
                }
                else if (type == typeof(double))
                {
                    typeInfo = ScalarTypeInfo.Double();
                    return true;
                }
                else if (type == typeof(Guid))
                {
                    typeInfo = ScalarTypeInfo.Guid();
                    return true;
                }
                else
                {
                    typeInfo = null;
                    return false;
                }
            }


            // Copy src to buffer and modify the offset.
            // Note: We know the buffer size ahead of time to make sure no buffer overflow.
            internal static unsafe void WriteToBuffer(byte* buffer, uint bufferLength, ref uint offset, byte* src, uint srcLength)
            {
                for (int i = 0; i < srcLength; i++)
                {
                    *(byte*)(buffer + offset + i) = *(byte*)(src + i);
                }
                offset += srcLength;
            }

            internal static unsafe void WriteToBuffer<T>(byte* buffer, uint bufferLength, ref uint offset, T value) where T : unmanaged
            {
                *(T*)(buffer + offset) = value;
                offset += (uint)sizeof(T);
            }

            public byte[]? GenerateEventMetadata(EventSourceEvent evt)
            {
                EventParameterInfo[] eventParams = new EventParameterInfo[evt.Parameters!.Count];
                int paramNum = 0;
                foreach (var param in evt.Parameters)
                {
                    var ti = GetTypeInfo(param, out TraceLoggingTypeInfo? paramTypeInfo);
                    eventParams[paramNum++].SetInfo(param.Name, param.TypeString, paramTypeInfo);
                }
                for (int i = 0; i < eventParams.Length; i++)
                {
                    EventParameterInfo.GetTypeInfoFromType(parameters[i].ParameterType, out TraceLoggingTypeInfo? paramTypeInfo);
                    eventParams[i].SetInfo(parameters[i].Name!, parameters[i].ParameterType, paramTypeInfo);
                }

                return GenerateMetadata(
                    eventMetadata.Descriptor.EventId,
                    eventMetadata.Name,
                    eventMetadata.Descriptor.Keywords,
                    eventMetadata.Descriptor.Level,
                    eventMetadata.Descriptor.Version,
                    (EventOpcode)eventMetadata.Descriptor.Opcode,
                    eventParams);
            }
        }

    }
}

        public byte[]? GenerateEventMetadata(EventMetadata eventMetadata)
        {
            ParameterInfo[] parameters = eventMetadata.Parameters;
            EventParameterInfo[] eventParams = new EventParameterInfo[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                EventParameterInfo.GetTypeInfoFromType(parameters[i].ParameterType, out TraceLoggingTypeInfo? paramTypeInfo);
                eventParams[i].SetInfo(parameters[i].Name!, parameters[i].ParameterType, paramTypeInfo);
            }

            return GenerateMetadata(
                eventMetadata.Descriptor.EventId,
                eventMetadata.Name,
                eventMetadata.Descriptor.Keywords,
                eventMetadata.Descriptor.Level,
                eventMetadata.Descriptor.Version,
                (EventOpcode)eventMetadata.Descriptor.Opcode,
                eventParams);
        }

        public byte[]? GenerateEventMetadata(
            int eventId,
            string eventName,
            EventKeywords keywords,
            EventLevel level,
            uint version,
            EventOpcode opcode,
            TraceLoggingEventTypes eventTypes)
        {
            TraceLoggingTypeInfo[] typeInfos = eventTypes.typeInfos;
            string[]? paramNames = eventTypes.paramNames;
            EventParameterInfo[] eventParams = new EventParameterInfo[typeInfos.Length];
            for (int i = 0; i < typeInfos.Length; i++)
            {
                string paramName = string.Empty;
                if (paramNames != null)
                {
                    paramName = paramNames[i];
                }
                eventParams[i].SetInfo(paramName, typeInfos[i].DataType, typeInfos[i]);
            }

            return GenerateMetadata(eventId, eventName, (long)keywords, (uint)level, version, opcode, eventParams);
        }

        internal unsafe byte[]? GenerateMetadata(
            int eventId,
            string eventName,
            long keywords,
            uint level,
            uint version,
            EventOpcode opcode,
            EventParameterInfo[] parameters)
        {
            byte[]? metadata = null;
            bool hasV2ParameterTypes = false;
            try
            {
                // eventID          : 4 bytes
                // eventName        : (eventName.Length + 1) * 2 bytes
                // keywords         : 8 bytes
                // eventVersion     : 4 bytes
                // level            : 4 bytes
                // parameterCount   : 4 bytes
                uint v1MetadataLength = 24 + ((uint)eventName.Length + 1) * 2;
                uint v2MetadataLength = 0;
                uint defaultV1MetadataLength = v1MetadataLength;

                // Check for an empty payload.
                // Write<T> calls with no arguments by convention have a parameter of
                // type NullTypeInfo which is serialized as nothing.
                if ((parameters.Length == 1) && (parameters[0].ParameterType == typeof(EmptyStruct)))
                {
                    parameters = Array.Empty<EventParameterInfo>();
                }

                // Increase the metadataLength for parameters.
                foreach (EventParameterInfo parameter in parameters)
                {
                    uint pMetadataLength;
                    if (!parameter.GetMetadataLength(out pMetadataLength))
                    {
                        // The call above may return false which means it is an unsupported type for V1.
                        // If that is the case we use the v2 blob for metadata instead
                        hasV2ParameterTypes = true;
                        break;
                    }

                    v1MetadataLength += (uint)pMetadataLength;
                }


                if (hasV2ParameterTypes)
                {
                    v1MetadataLength = defaultV1MetadataLength;

                    // V2 length is the parameter count (4 bytes) plus the size of the params
                    v2MetadataLength = 4;
                    foreach (EventParameterInfo parameter in parameters)
                    {
                        uint pMetadataLength;
                        if (!parameter.GetMetadataLengthV2(out pMetadataLength))
                        {
                            // We ran in to an unsupported type, return empty event metadata
                            parameters = Array.Empty<EventParameterInfo>();
                            v1MetadataLength = defaultV1MetadataLength;
                            v2MetadataLength = 0;
                            hasV2ParameterTypes = false;
                            break;
                        }

                        v2MetadataLength += (uint)pMetadataLength;
                    }
                }

                // Optional opcode length needs 1 byte for the opcode + 5 bytes for the tag (4 bytes size, 1 byte kind)
                uint opcodeMetadataLength = opcode == EventOpcode.Info ? 0u : 6u;
                // Optional V2 metadata needs the size of the params + 5 bytes for the tag (4 bytes size, 1 byte kind)
                uint v2MetadataPayloadLength = v2MetadataLength == 0 ? 0 : v2MetadataLength + 5;
                uint totalV2MetadataLength = v2MetadataPayloadLength + opcodeMetadataLength;
                uint totalMetadataLength = v1MetadataLength + totalV2MetadataLength;
                metadata = new byte[totalMetadataLength];

                // Write metadata: metadataHeaderLength, eventID, eventName, keywords, eventVersion, level,
                //                 parameterCount, param1..., optional extended metadata
                fixed (byte* pMetadata = metadata)
                {
                    uint offset = 0;

                    WriteToBuffer(pMetadata, totalMetadataLength, ref offset, (uint)eventId);
                    fixed (char* pEventName = eventName)
                    {
                        WriteToBuffer(pMetadata, totalMetadataLength, ref offset, (byte*)pEventName, ((uint)eventName.Length + 1) * 2);
                    }
                    WriteToBuffer(pMetadata, totalMetadataLength, ref offset, keywords);
                    WriteToBuffer(pMetadata, totalMetadataLength, ref offset, version);
                    WriteToBuffer(pMetadata, totalMetadataLength, ref offset, level);

                    if (hasV2ParameterTypes)
                    {
                        // If we have unsupported types, the V1 metadata must be empty. Write 0 count of params.
                        WriteToBuffer(pMetadata, totalMetadataLength, ref offset, 0);
                    }
                    else
                    {
                        // Without unsupported V1 types we can write all the params now.
                        WriteToBuffer(pMetadata, totalMetadataLength, ref offset, (uint)parameters.Length);
                        foreach (var parameter in parameters)
                        {
                            if (!parameter.GenerateMetadata(pMetadata, ref offset, totalMetadataLength))
                            {
                                // If we fail to generate metadata for any parameter, we should return the "default" metadata without any parameters
                                return GenerateMetadata(eventId, eventName, keywords, level, version, opcode, Array.Empty<EventParameterInfo>());
                            }
                        }
                    }

                    Debug.Assert(offset == v1MetadataLength);

                    if (opcode != EventOpcode.Info)
                    {
                        // Size of opcode
                        WriteToBuffer(pMetadata, totalMetadataLength, ref offset, 1);
                        WriteToBuffer(pMetadata, totalMetadataLength, ref offset, (byte)MetadataTag.Opcode);
                        WriteToBuffer(pMetadata, totalMetadataLength, ref offset, (byte)opcode);
                    }

                    if (hasV2ParameterTypes)
                    {
                        // Write the V2 supported metadata now
                        // Starting with the size of the V2 payload
                        WriteToBuffer(pMetadata, totalMetadataLength, ref offset, v2MetadataLength);
                        // Now the tag to identify it as a V2 parameter payload
                        WriteToBuffer(pMetadata, totalMetadataLength, ref offset, (byte)MetadataTag.ParameterPayload);
                        // Then the count of parameters
                        WriteToBuffer(pMetadata, totalMetadataLength, ref offset, (uint)parameters.Length);
                        // Finally the parameters themselves
                        foreach (var parameter in parameters)
                        {
                            if (!parameter.GenerateMetadataV2(pMetadata, ref offset, totalMetadataLength))
                            {
                                // If we fail to generate metadata for any parameter, we should return the "default" metadata without any parameters
                                return GenerateMetadata(eventId, eventName, keywords, level, version, opcode, Array.Empty<EventParameterInfo>());
                            }
                        }
                    }

                    Debug.Assert(totalMetadataLength == offset);
                }
            }
            catch
            {
                // If a failure occurs during metadata generation, make sure that we don't return
                // malformed metadata.  Instead, return a null metadata blob.
                // Consumers can either build in knowledge of the event or skip it entirely.
                metadata = null;
            }

            return metadata;
        }

        // Copy src to buffer and modify the offset.
        // Note: We know the buffer size ahead of time to make sure no buffer overflow.
        internal static unsafe void WriteToBuffer(byte* buffer, uint bufferLength, ref uint offset, byte* src, uint srcLength)
        {
            Debug.Assert(bufferLength >= (offset + srcLength));
            for (int i = 0; i < srcLength; i++)
            {
                *(byte*)(buffer + offset + i) = *(byte*)(src + i);
            }
            offset += srcLength;
        }

        internal static unsafe void WriteToBuffer<T>(byte* buffer, uint bufferLength, ref uint offset, T value) where T : unmanaged
        {
            Debug.Assert(bufferLength >= (offset + sizeof(T)));
            *(T*)(buffer + offset) = value;
            offset += (uint)sizeof(T);
        }
    }

    internal struct EventParameterInfo
    {
        internal string ParameterName;
        internal Type ParameterType;
        internal string ParameterTypeName;
        internal TraceLoggingTypeInfo? TypeInfo;

        internal void SetInfo(string name, string typeName, TraceLoggingTypeInfo? typeInfo = null)
        {
            ParameterName = name;
            ParameterTypeName = typeName;
            TypeInfo = typeInfo;
        }

        internal unsafe bool GenerateMetadata(byte* pMetadataBlob, ref uint offset, uint blobSize)
        {
            TypeCode typeCode = GetTypeCodeExtended(ParameterType);
            if (typeCode == TypeCode.Object)
            {
                // Each nested struct is serialized as:
                //     TypeCode.Object              : 4 bytes
                //     Number of properties         : 4 bytes
                //     Property description 0...N
                //     Nested struct property name  : NULL-terminated string.
                EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)TypeCode.Object);

                if (!(TypeInfo is InvokeTypeInfo invokeTypeInfo))
                {
                    return false;
                }

                // Get the set of properties to be serialized.
                PropertyAnalysis[]? properties = invokeTypeInfo.properties;
                if (properties != null)
                {
                    // Write the count of serializable properties.
                    EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)properties.Length);

                    foreach (PropertyAnalysis prop in properties)
                    {
                        if (!GenerateMetadataForProperty(prop, pMetadataBlob, ref offset, blobSize))
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    // This struct has zero serializable properties so we just write the property count.
                    EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)0);
                }

                // Top-level structs don't have a property name, but for simplicity we write a NULL-char to represent the name.
                EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, '\0');
            }
            else
            {
                // Write parameter type.
                EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)typeCode);

                // Write parameter name.
                fixed (char* pParameterName = ParameterName)
                {
                    EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (byte*)pParameterName, ((uint)ParameterName.Length + 1) * 2);
                }
            }
            return true;
        }

        private static unsafe bool GenerateMetadataForProperty(PropertyAnalysis property, byte* pMetadataBlob, ref uint offset, uint blobSize)
        {
            Debug.Assert(property != null);
            Debug.Assert(pMetadataBlob != null);

            // Check if this property is a nested struct.
            if (property.typeInfo is InvokeTypeInfo invokeTypeInfo)
            {
                // Each nested struct is serialized as:
                //     TypeCode.Object              : 4 bytes
                //     Number of properties         : 4 bytes
                //     Property description 0...N
                //     Nested struct property name  : NULL-terminated string.
                EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)TypeCode.Object);

                // Get the set of properties to be serialized.
                PropertyAnalysis[]? properties = invokeTypeInfo.properties;
                if (properties != null)
                {
                    // Write the count of serializable properties.
                    EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)properties.Length);

                    foreach (PropertyAnalysis prop in properties)
                    {
                        if (!GenerateMetadataForProperty(prop, pMetadataBlob, ref offset, blobSize))
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    // This struct has zero serializable properties so we just write the property count.
                    EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)0);
                }

                // Write the property name.
                fixed (char* pPropertyName = property.name)
                {
                    EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (byte*)pPropertyName, ((uint)property.name.Length + 1) * 2);
                }
            }
            else
            {
                // Each primitive type is serialized as:
                //     TypeCode : 4 bytes
                //     PropertyName : NULL-terminated string
                TypeCode typeCode = GetTypeCodeExtended(property.typeInfo.DataType);

                // EventPipe does not support this type.  Throw, which will cause no metadata to be registered for this event.
                if (typeCode == TypeCode.Object)
                {
                    return false;
                }

                // Write the type code.
                EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)typeCode);

                // Write the property name.
                fixed (char* pPropertyName = property.name)
                {
                    EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (byte*)pPropertyName, ((uint)property.name.Length + 1) * 2);
                }
            }
            return true;
        }

        internal unsafe bool GenerateMetadataV2(byte* pMetadataBlob, ref uint offset, uint blobSize)
        {
            if (TypeInfo == null)
                return false;
            return GenerateMetadataForNamedTypeV2(ParameterName, TypeInfo, pMetadataBlob, ref offset, blobSize);
        }

        private static unsafe bool GenerateMetadataForNamedTypeV2(string name, TraceLoggingTypeInfo typeInfo, byte* pMetadataBlob, ref uint offset, uint blobSize)
        {
            Debug.Assert(pMetadataBlob != null);

            if (!GetMetadataLengthForNamedTypeV2(name, typeInfo, out uint length))
            {
                return false;
            }

            EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, length);

            // Write the property name.
            fixed (char *pPropertyName = name)
            {
                EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (byte *)pPropertyName, ((uint)name.Length + 1) * 2);
            }

            return GenerateMetadataForTypeV2(typeInfo, pMetadataBlob, ref offset, blobSize);
        }

        private static unsafe bool GenerateMetadataForTypeV2(TraceLoggingTypeInfo? typeInfo, byte* pMetadataBlob, ref uint offset, uint blobSize)
        {
            Debug.Assert(typeInfo != null);
            Debug.Assert(pMetadataBlob != null);

            // Check if this type is a nested struct.
            if (typeInfo is InvokeTypeInfo invokeTypeInfo)
            {
                // Each nested struct is serialized as:
                //     TypeCode.Object              : 4 bytes
                //     Number of properties         : 4 bytes
                //     Property description 0...N
                EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)TypeCode.Object);

                // Get the set of properties to be serialized.
                PropertyAnalysis[]? properties = invokeTypeInfo.properties;
                if (properties != null)
                {
                    // Write the count of serializable properties.
                    EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)properties.Length);

                    foreach (PropertyAnalysis prop in properties)
                    {
                        if (!GenerateMetadataForNamedTypeV2(prop.name, prop.typeInfo, pMetadataBlob, ref offset, blobSize))
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    // This struct has zero serializable properties so we just write the property count.
                    EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)0);
                }
            }
            else if (typeInfo is EnumerableTypeInfo enumerableTypeInfo)
            {
                // Each enumerable is serialized as:
                //     TypeCode.Array               : 4 bytes
                //     ElementType                  : N bytes
                EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, EventPipeTypeCodeArray);
                GenerateMetadataForTypeV2(enumerableTypeInfo.ElementInfo, pMetadataBlob, ref offset, blobSize);
            }
            else if (typeInfo is ScalarArrayTypeInfo arrayTypeInfo)
            {
                // Each enumerable is serialized as:
                //     TypeCode.Array               : 4 bytes
                //     ElementType                  : N bytes
                if (!arrayTypeInfo.DataType.HasElementType)
                {
                    return false;
                }

                TraceLoggingTypeInfo? elementTypeInfo;
                if (!GetTypeInfoFromType(arrayTypeInfo.DataType.GetElementType(), out elementTypeInfo))
                {
                    return false;
                }

                EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, EventPipeTypeCodeArray);
                GenerateMetadataForTypeV2(elementTypeInfo, pMetadataBlob, ref offset, blobSize);
            }
            else
            {
                // Each primitive type is serialized as:
                //     TypeCode : 4 bytes
                TypeCode typeCode = GetTypeCodeExtended(typeInfo.DataType);

                // EventPipe does not support this type.  Throw, which will cause no metadata to be registered for this event.
                if (typeCode == TypeCode.Object)
                {
                    return false;
                }

                // Write the type code.
                EventPipeMetadataGenerator.WriteToBuffer(pMetadataBlob, blobSize, ref offset, (uint)typeCode);
            }
            return true;
        }

        internal static bool GetTypeInfoFromType(Type? type, out TraceLoggingTypeInfo? typeInfo)
        {
            if (type == typeof(bool))
            {
                typeInfo = ScalarTypeInfo.Boolean();
                return true;
            }
            else if (type == typeof(byte))
            {
                typeInfo = ScalarTypeInfo.Byte();
                return true;
            }
            else if (type == typeof(sbyte))
            {
                typeInfo = ScalarTypeInfo.SByte();
                return true;
            }
            else if (type == typeof(char))
            {
                typeInfo = ScalarTypeInfo.Char();
                return true;
            }
            else if (type == typeof(short))
            {
                typeInfo = ScalarTypeInfo.Int16();
                return true;
            }
            else if (type == typeof(ushort))
            {
                typeInfo = ScalarTypeInfo.UInt16();
                return true;
            }
            else if (type == typeof(int))
            {
                typeInfo = ScalarTypeInfo.Int32();
                return true;
            }
            else if (type == typeof(uint))
            {
                typeInfo = ScalarTypeInfo.UInt32();
                return true;
            }
            else if (type == typeof(long))
            {
                typeInfo = ScalarTypeInfo.Int64();
                return true;
            }
            else if (type == typeof(ulong))
            {
                typeInfo = ScalarTypeInfo.UInt64();
                return true;
            }
            else if (type == typeof(IntPtr))
            {
                typeInfo = ScalarTypeInfo.IntPtr();
                return true;
            }
            else if (type == typeof(UIntPtr))
            {
                typeInfo = ScalarTypeInfo.UIntPtr();
                return true;
            }
            else if (type == typeof(float))
            {
                typeInfo = ScalarTypeInfo.Single();
                return true;
            }
            else if (type == typeof(double))
            {
                typeInfo = ScalarTypeInfo.Double();
                return true;
            }
            else if (type == typeof(Guid))
            {
                typeInfo = ScalarTypeInfo.Guid();
                return true;
            }
            else
            {
                typeInfo = null;
                return false;
            }
        }

        internal bool GetMetadataLength(out uint size)
        {
            size = 0;

            TypeCode typeCode = GetTypeCodeExtended(ParameterType);
            if (typeCode == TypeCode.Object)
            {
                if (!(TypeInfo is InvokeTypeInfo typeInfo))
                {
                    return false;
                }

                // Each nested struct is serialized as:
                //     TypeCode.Object      : 4 bytes
                //     Number of properties : 4 bytes
                //     Property description 0...N
                //     Nested struct property name  : NULL-terminated string.
                size += sizeof(uint)  // TypeCode
                     + sizeof(uint); // Property count

                // Get the set of properties to be serialized.
                PropertyAnalysis[]? properties = typeInfo.properties;
                if (properties != null)
                {
                    foreach (PropertyAnalysis prop in properties)
                    {
                        size += GetMetadataLengthForProperty(prop);
                    }
                }

                // For simplicity when writing a reader, we write a NULL char
                // after the metadata for a top-level struct (for its name) so that
                // readers don't have do special case the outer-most struct.
                size += sizeof(char);
            }
            else
            {
                size += (uint)(sizeof(uint) + ((ParameterName.Length + 1) * 2));
            }

            return true;
        }

        private static uint GetMetadataLengthForProperty(PropertyAnalysis property)
        {
            Debug.Assert(property != null);

            uint ret = 0;

            // Check if this property is a nested struct.
            if (property.typeInfo is InvokeTypeInfo invokeTypeInfo)
            {
                // Each nested struct is serialized as:
                //     TypeCode.Object      : 4 bytes
                //     Number of properties : 4 bytes
                //     Property description 0...N
                //     Nested struct property name  : NULL-terminated string.
                ret += sizeof(uint)  // TypeCode
                     + sizeof(uint); // Property count

                // Get the set of properties to be serialized.
                PropertyAnalysis[]? properties = invokeTypeInfo.properties;
                if (properties != null)
                {
                    foreach (PropertyAnalysis prop in properties)
                    {
                        ret += GetMetadataLengthForProperty(prop);
                    }
                }

                // Add the size of the property name.
                ret += (uint)((property.name.Length + 1) * 2);
            }
            else
            {
                ret += (uint)(sizeof(uint) + ((property.name.Length + 1) * 2));
            }

            return ret;
        }

        // Array is not part of TypeCode, we decided to use 19 to represent it. (18 is the last type code value, string)
        private const int EventPipeTypeCodeArray = 19;

        private static TypeCode GetTypeCodeExtended(Type parameterType)
        {
            // Guid is not part of TypeCode, we decided to use 17 to represent it, as it's the "free slot"
            // see https://github.com/dotnet/runtime/issues/9629#issuecomment-361749750 for more
            const TypeCode GuidTypeCode = (TypeCode)17;

            if (parameterType == typeof(Guid)) // Guid is not a part of TypeCode enum
                return GuidTypeCode;

            // IntPtr and UIntPtr are converted to their non-pointer types.
            if (parameterType == typeof(IntPtr))
                return IntPtr.Size == 4 ? TypeCode.Int32 : TypeCode.Int64;

            if (parameterType == typeof(UIntPtr))
                return UIntPtr.Size == 4 ? TypeCode.UInt32 : TypeCode.UInt64;

            return Type.GetTypeCode(parameterType);
        }

        internal bool GetMetadataLengthV2(out uint size)
        {
            return GetMetadataLengthForNamedTypeV2(ParameterName, TypeInfo, out size);
        }

        private static bool GetMetadataLengthForTypeV2(TraceLoggingTypeInfo? typeInfo, out uint size)
        {
            size = 0;
            if (typeInfo == null)
            {
                return false;
            }

            if (typeInfo is InvokeTypeInfo invokeTypeInfo)
            {
                // Struct is serialized as:
                //     TypeCode.Object      : 4 bytes
                //     Number of properties : 4 bytes
                //     Property description 0...N
                size += sizeof(uint)  // TypeCode
                     + sizeof(uint); // Property count

                // Get the set of properties to be serialized.
                PropertyAnalysis[]? properties = invokeTypeInfo.properties;
                if (properties != null)
                {
                    foreach (PropertyAnalysis prop in properties)
                    {
                        if (!GetMetadataLengthForNamedTypeV2(prop.name, prop.typeInfo, out uint typeSize))
                        {
                            return false;
                        }

                        size += typeSize;
                    }
                }
            }
            else if (typeInfo is EnumerableTypeInfo enumerableTypeInfo)
            {
                // IEnumerable<T> is serialized as:
                //     TypeCode            : 4 bytes
                //     ElementType         : N bytes
                size += sizeof(uint);
                if (!GetMetadataLengthForTypeV2(enumerableTypeInfo.ElementInfo, out uint typeSize))
                {
                    return false;
                }

                size += typeSize;
            }
            else if (typeInfo is ScalarArrayTypeInfo arrayTypeInfo)
            {
                TraceLoggingTypeInfo? elementTypeInfo;
                if (!arrayTypeInfo.DataType.HasElementType
                    || !GetTypeInfoFromType(arrayTypeInfo.DataType.GetElementType(), out elementTypeInfo))
                {
                    return false;
                }

                size += sizeof(uint);
                if (!GetMetadataLengthForTypeV2(elementTypeInfo, out uint typeSize))
                {
                    return false;
                }

                size += typeSize;
            }
            else
            {
                size += (uint)sizeof(uint);
            }

            return true;
        }

        private static bool GetMetadataLengthForNamedTypeV2(string name, TraceLoggingTypeInfo? typeInfo, out uint size)
        {
            // Named type is serialized
            //     SizeOfTypeDescription    : 4 bytes
            //     Name                     : NULL-terminated UTF16 string
            //     Type                     : N bytes
            size = (uint)(sizeof(uint) +
                   ((name.Length + 1) * 2));

            if (!GetMetadataLengthForTypeV2(typeInfo, out uint typeSize))
            {
                return false;
            }

            size += typeSize;
            return true;
        }

            /// <summary>
    /// TraceLogging: used when implementing a custom TraceLoggingTypeInfo.
    /// Non-generic base class for TraceLoggingTypeInfo&lt;DataType>. Do not derive
    /// from this class. Instead, derive from TraceLoggingTypeInfo&lt;DataType>.
    /// </summary>
    internal abstract class TraceLoggingTypeInfo
    {
        private readonly string name;
        private readonly EventKeywords keywords;
        private readonly EventLevel level = (EventLevel)(-1);
        private readonly EventOpcode opcode = (EventOpcode)(-1);
        private readonly EventTags tags;
        private readonly Type dataType;
        private readonly Func<object?, PropertyValue> propertyValueFactory;

        internal TraceLoggingTypeInfo(Type dataType)
        {
            if (dataType == null)
            {
                throw new ArgumentNullException(nameof(dataType));
            }

            this.name = dataType.Name;
            this.dataType = dataType;
            this.propertyValueFactory = PropertyValue.GetFactory(dataType);
        }

        internal TraceLoggingTypeInfo(
            Type dataType,
            string name,
            EventLevel level,
            EventOpcode opcode,
            EventKeywords keywords,
            EventTags tags)
        {
            if (dataType == null)
            {
                throw new ArgumentNullException(nameof(dataType));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            Statics.CheckName(name);

            this.name = name;
            this.keywords = keywords;
            this.level = level;
            this.opcode = opcode;
            this.tags = tags;
            this.dataType = dataType;
            this.propertyValueFactory = PropertyValue.GetFactory(dataType);
        }

        /// <summary>
        /// Gets the name to use for the event if this type is the top-level type,
        /// or the name to use for an implicitly-named field.
        /// Never null.
        /// </summary>
        public string Name => this.name;

        /// <summary>
        /// Gets the event level associated with this type. Any value in the range 0..255
        /// is an associated event level. Any value outside the range 0..255 is invalid and
        /// indicates that this type has no associated event level.
        /// </summary>
        public EventLevel Level => this.level;

        /// <summary>
        /// Gets the event opcode associated with this type. Any value in the range 0..255
        /// is an associated event opcode. Any value outside the range 0..255 is invalid and
        /// indicates that this type has no associated event opcode.
        /// </summary>
        public EventOpcode Opcode => this.opcode;

        /// <summary>
        /// Gets the keyword(s) associated with this type.
        /// </summary>
        public EventKeywords Keywords => this.keywords;

        /// <summary>
        /// Gets the event tags associated with this type.
        /// </summary>
        public EventTags Tags => this.tags;

        internal Type DataType => this.dataType;

        internal Func<object?, PropertyValue> PropertyValueFactory => this.propertyValueFactory;

        /// <summary>
        /// When overridden by a derived class, writes the metadata (schema) for
        /// this type. Note that the sequence of operations in WriteMetadata should be
        /// essentially identical to the sequence of operations in
        /// WriteData/WriteObjectData. Otherwise, the metadata and data will not match,
        /// which may cause trouble when decoding the event.
        /// </summary>
        /// <param name="collector">
        /// The object that collects metadata for this object's type. Metadata is written
        /// by calling methods on the collector object. Note that if the type contains
        /// sub-objects, the implementation of this method may need to call the
        /// WriteMetadata method for the type of the sub-object, e.g. by calling
        /// TraceLoggingTypeInfo&lt;SubType&gt;.Instance.WriteMetadata(...).
        /// </param>
        /// <param name="name">
        /// The name of the property that contains an object of this type, or null if this
        /// object is being written as a top-level object of an event. Typical usage
        /// is to pass this value to collector.AddGroup.
        /// </param>
        /// <param name="format">
        /// The format attribute for the field that contains an object of this type.
        /// </param>
        public abstract void WriteMetadata(
            TraceLoggingMetadataCollector collector,
            string? name,
            EventFieldFormat format);

        /// <summary>
        /// Refer to TraceLoggingTypeInfo.WriteObjectData for information about this
        /// method.
        /// </summary>
        /// <param name="value">
        /// Refer to TraceLoggingTypeInfo.WriteObjectData for information about this
        /// method.
        /// </param>
        public abstract void WriteData(PropertyValue value);

        /// <summary>
        /// Fetches the event parameter data for internal serialization.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public virtual object? GetData(object? value)
        {
            return value;
        }

        [ThreadStatic] // per-thread cache to avoid synchronization
        private static Dictionary<Type, TraceLoggingTypeInfo>? threadCache;

#if !ES_BUILD_STANDALONE
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("EventSource WriteEvent will serialize the whole object graph. Trimmer will not safely handle this case because properties may be trimmed. This can be suppressed if the object is a primitive type")]
#endif
        public static TraceLoggingTypeInfo GetInstance(Type type, List<Type>? recursionCheck)
        {
            Dictionary<Type, TraceLoggingTypeInfo> cache = threadCache ??= new Dictionary<Type, TraceLoggingTypeInfo>();

            if (!cache.TryGetValue(type, out TraceLoggingTypeInfo? instance))
            {
                recursionCheck ??= new List<Type>();
                int recursionCheckCount = recursionCheck.Count;
                instance = Statics.CreateDefaultTypeInfo(type, recursionCheck);
                cache[type] = instance;
                recursionCheck.RemoveRange(recursionCheckCount, recursionCheck.Count - recursionCheckCount);
            }
            return instance;
        }
    }
    }