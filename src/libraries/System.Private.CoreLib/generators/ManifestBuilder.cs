﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Generators
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
        public ManifestBuilder(StringBuilder builder, string providerName, Guid providerGuid)
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
               Append("\" guid=\"{{").Append(providerGuid.ToString()).Append("}}");
               /*
            if (dllName != null)
                sb.Append("\" resourceFileName=\"").Append(dllName).Append("\" messageFileName=\"").Append(dllName);
                */
            string symbolsName = providerName.Replace("-", "").Replace('.', '_');  // Period and - are illegal replace them.
            sb.Append("\" symbol=\"").Append(symbolsName);
            sb.AppendLine("\">");
        }

        public void AddOpcode(string name, int value)
        {
            opcodeTab[value] = name;
        }

        public void AddTask(string name, int value)
        {
            taskTab ??= new Dictionary<int, string>();
            taskTab[value] = name;
        }

        public void AddKeyword(string name, ulong value)
        {
            if ((value & (value - 1)) != 0)   // Is it a power of 2?
            {
                LogManifestGenerationError("Error while adding Keyword  - value is not power of 2.");
                //ManifestError(SR.Format(SR.EventSource_KeywordNeedPowerOfTwo, "0x" + value.ToString("x", CultureInfo.CurrentCulture), name), true);
            }
            keywordTab ??= new Dictionary<ulong, string>();
            keywordTab[value] = name;
        }

        public void StartEvent(string eventName, EventAttribute eventAttribute)
        {
            this.eventName = eventName;
            this.numParams = 0;
            byteArrArgIndices = null;
            
            events.Append("  <event value=\"").Append(eventAttribute.EventId).
                 Append("\" version=\"").Append(eventAttribute.Version).
                 Append("\" level=\"");
            AppendLevelName(events, eventAttribute.Level);
            events.Append("\" symbol=\"").Append(eventName).Append('"');
            // at this point we add to the manifest's stringTab a message that is as-of-yet
            // "untranslated to manifest convention", b/c we don't have the number or position
            // of any byte[] args (which require string format index updates)
            // WriteMessageAttrib(events, "event", eventName, eventAttribute.Message);
/*
            if (eventAttribute.Keywords != 0)
            {
                events.Append(" keywords=\"");
                AppendKeywords(events, (ulong)eventAttribute.Keywords, eventName);
                events.Append('"');
            }
            if (eventAttribute.Opcode != 0)
            {
                events.Append(" opcode=\"").Append(GetOpcodeName(eventAttribute.Opcode, eventName)).Append('"');
            }

            if (eventAttribute.Task != 0)
            {
                events.Append(" task=\"").Append(GetTaskName(eventAttribute.Task, eventName)).Append('"');
            }

            if (eventAttribute.Channel != 0)
            {
                events.Append(" channel=\"").Append(GetChannelName(eventAttribute.Channel, eventName, eventAttribute.Message)).Append('"');
            }
*/
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
            // TODO: for 'byte*' types it assumes the user provided length is named using the same naming convention
            //       as for 'byte[]' args (blob_arg_name + "Size")
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

            // See if the user wants things localized.
            /*
            if (resources != null)
            {
                // resource fallback: strings in the neutral culture will take precedence over inline strings
                key = elementName + "_" + name;
                if (resources.GetString(key, CultureInfo.InvariantCulture) is string localizedString)
                    value = localizedString;
            }
            */

            if (value == null)
                return;

            key ??= elementName + "_" + name;
            stringBuilder.Append(" message=\"$(string.").Append(key).Append(")\"");

            if (stringTab.TryGetValue(key, out string? prevValue) && !prevValue.Equals(value))
            {
                LogManifestGenerationError("WriteMessageAtrib - Duplicate string key manifest error");
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

            sb.Append(level switch // avoid boxing that comes from level.ToString()
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

/*
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
*/
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
                    else
                    {
                        LogManifestGenerationError("TranslateToManifestConvention - Unsupported message property");
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


        public byte[] CreateManifest()
        {
            string str = CreateManifestString();
            return Encoding.UTF8.GetBytes(str);
        }

        public IList<string> Errors => errors;

        /// <summary>
        /// When validating an event source it adds the error to the error collection.
        /// When not validating it throws an exception if runtimeCritical is "true".
        /// Otherwise the error is ignored.
        /// </summary>
        /// <param name="msg"></param>
        /// <param name="runtimeCritical"></param>
        public void ManifestError(string msg, bool runtimeCritical = false)
        {
//            if ((flags & EventManifestOptions.Strict) != 0)
//                errors.Add(msg);
            //else if (runtimeCritical)
            //    throw new ArgumentException(msg);
        }

        public string CreateManifestString()
        {
            // TODO: Add maps, keywords, tasks, channels generation logic.
            sb.AppendLine(" <events>");
            sb.Append(events.ToString());
            sb.AppendLine(" </events>");
            sb.Append(templates.ToString());
            return sb.ToString();
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
                        LogManifestGenerationError("AppendKeywords: Keyword is null");
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


/*        private string CreateManifestString()
        {
            Span<char> ulongHexScratch = stackalloc char[16]; // long enough for ulong.MaxValue formatted as hex

            // Write out the channels
            if (channelTab != null)
            {
                sb.AppendLine(" <channels>");
                var sortedChannels = new List<KeyValuePair<int, ChannelInfo>>();
                foreach (KeyValuePair<int, ChannelInfo> p in channelTab) { sortedChannels.Add(p); }
                sortedChannels.Sort((p1, p2) => -Comparer<ulong>.Default.Compare(p1.Value.Keywords, p2.Value.Keywords));
                foreach (KeyValuePair<int, ChannelInfo> kvpair in sortedChannels)
                {
                    int channel = kvpair.Key;
                    ChannelInfo channelInfo = kvpair.Value;

                    string? channelType = null;
                    bool enabled = false;
                    string? fullName = null;
                    string? isolation = null;
                    string? access = null;

                    if (channelInfo.Attribs != null)
                    {
                        EventChannelAttribute attribs = channelInfo.Attribs;
                        if (Enum.IsDefined(typeof(EventChannelType), attribs.EventChannelType))
                            channelType = attribs.EventChannelType.ToString();
                        enabled = attribs.Enabled;
                        if (attribs.ImportChannel != null)
                        {
                            fullName = attribs.ImportChannel;
                            elementName = "importChannel";
                        }
                        if (Enum.IsDefined(typeof(EventChannelIsolation), attribs.Isolation))
                            isolation = attribs.Isolation.ToString();
                        access = attribs.Access;
                    }

                    fullName ??= providerName + "/" + channelInfo.Name;

                    sb.Append("  <channel chid=\"").Append(channelInfo.Name).Append("\" name=\"").Append(fullName).Append('"');

                    Debug.Assert(channelInfo.Name != null);
                    WriteMessageAttrib(sb, "channel", channelInfo.Name, null);
                    sb.Append(" value=\"").Append(channel).Append('"');
                    if (channelType != null)
                        sb.Append(" type=\"").Append(channelType).Append('"');
                    sb.Append(" enabled=\"").Append(enabled ? "true" : "false").Append('"');
                    if (access != null)
                        sb.Append(" access=\"").Append(access).Append("\"");
                    if (isolation != null)
                        sb.Append(" isolation=\"").Append(isolation).Append("\"");
                    sb.AppendLine("/>");
                }
                sb.AppendLine(" </channels>");
            }

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

            // Scoping the call to enum GetFields to a local function to limit the linker suppression
            [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2070:UnrecognizedReflectionPattern",
            Justification = "Trimmer does not trim enums")]
            static FieldInfo[] GetEnumFields(Type localEnumType)
            {
                Debug.Assert(localEnumType.IsEnum);
                return localEnumType.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Static);
            }

            if (mapsTab != null)
            {
                sb.AppendLine(" <maps>");
                foreach (Type enumType in mapsTab.Values)
                {
                    bool isbitmap = EventSource.IsCustomAttributeDefinedHelper(enumType, typeof(FlagsAttribute), flags);
                    string mapKind = isbitmap ? "bitMap" : "valueMap";
                    sb.Append("  <").Append(mapKind).Append(" name=\"").Append(enumType.Name).AppendLine("\">");

                    // write out each enum value
                    FieldInfo[] staticFields = GetEnumFields(enumType);
                    bool anyValuesWritten = false;
                    foreach (FieldInfo staticField in staticFields)
                    {
                        object? constantValObj = staticField.GetRawConstantValue();

                        if (constantValObj != null)
                        {
                            ulong hexValue;
                            if (constantValObj is ulong)
                                hexValue = (ulong)constantValObj;    // This is the only integer type that can't be represented by a long.
                            else
                                hexValue = (ulong)Convert.ToInt64(constantValObj); // Handles all integer types except ulong.

                            // ETW requires all bitmap values to be powers of 2.  Skip the ones that are not.
                            // TODO: Warn people about the dropping of values.
                            if (isbitmap && ((hexValue & (hexValue - 1)) != 0 || hexValue == 0))
                                continue;

                            hexValue.TryFormat(ulongHexScratch, out int charsWritten, "x");
                            Span<char> hexValueFormatted = ulongHexScratch.Slice(0, charsWritten);
                            sb.Append("   <map value=\"0x").Append(hexValueFormatted).Append('"');
                            WriteMessageAttrib(sb, "map", enumType.Name + "." + staticField.Name, staticField.Name);
                            sb.AppendLine("/>");
                            anyValuesWritten = true;
                        }
                    }

                    // the OS requires that bitmaps and valuemaps have at least one value or it reject the whole manifest.
                    // To avoid that put a 'None' entry if there are no other values.
                    if (!anyValuesWritten)
                    {
                        sb.Append("   <map value=\"0x0\"");
                        WriteMessageAttrib(sb, "map", enumType.Name + ".None", "None");
                        sb.AppendLine("/>");
                    }
                    sb.Append("  </").Append(mapKind).AppendLine(">");
                }
                sb.AppendLine(" </maps>");
            }

            // Write out the opcodes
            sb.AppendLine(" <opcodes>");
            var sortedOpcodes = new List<int>(opcodeTab.Keys);
            sortedOpcodes.Sort();
            foreach (int opcode in sortedOpcodes)
            {
                sb.Append("  <opcode");
                WriteNameAndMessageAttribs(sb, "opcode", opcodeTab[opcode]);
                sb.Append(" value=\"").Append(opcode).AppendLine("\"/>");
            }
            sb.AppendLine(" </opcodes>");

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
                    keyword.TryFormat(ulongHexScratch, out int charsWritten, "x");
                    Span<char> keywordFormatted = ulongHexScratch.Slice(0, charsWritten);
                    sb.Append(" mask=\"0x").Append(keywordFormatted).AppendLine("\"/>");
                }
                sb.AppendLine(" </keywords>");
            }

            sb.AppendLine(" <events>");
            sb.Append(events);
            sb.AppendLine(" </events>");

            sb.AppendLine(" <templates>");
            if (templates.Length > 0)
            {
                sb.Append(templates);
            }
            else
            {
                // Work around a cornercase ETW issue where a manifest with no templates causes
                // ETW events to not get sent to their associated channel.
                sb.AppendLine("    <template tid=\"_empty\"></template>");
            }
            sb.AppendLine(" </templates>");

            sb.AppendLine("</provider>");
            sb.AppendLine("</events>");
            sb.AppendLine("</instrumentation>");

            // Output the localization information.
            sb.AppendLine("<localization>");

            var sortedStrings = new string[stringTab.Keys.Count];
            stringTab.Keys.CopyTo(sortedStrings, 0);
            Array.Sort<string>(sortedStrings, 0, sortedStrings.Length);

            CultureInfo ci = CultureInfo.CurrentUICulture;
            sb.Append(" <resources culture=\"").Append(ci.Name).AppendLine("\">");
            sb.AppendLine("  <stringTable>");
            foreach (string stringKey in sortedStrings)
            {
                string? val = GetLocalizedMessage(stringKey, ci, etwFormat: true);
                sb.Append("   <string id=\"").Append(stringKey).Append("\" value=\"").Append(val).AppendLine("\"/>");
            }
            sb.AppendLine("  </stringTable>");
            sb.AppendLine(" </resources>");

            sb.AppendLine("</localization>");
            sb.AppendLine("</instrumentationManifest>");
            return sb.ToString();
        }
*/
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
                    // TODO: error?
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

                    // ManifestError(SR.Format(SR.EventSource_UnsupportedEventTypeInManifest, type.Name), true);
                    // TODO: ERROR
                    return string.Empty;
            }
        }

        private static readonly string[] s_escapes = { "&amp;", "&lt;", "&gt;", "&apos;", "&quot;", "%r", "%n", "%t" };
        // Manifest messages use %N conventions for their message substitutions.   Translate from
        // .NET conventions.   We can't use RegEx for this (we are in mscorlib), so we do it 'by hand'


        // Used to log errors during manifest generation.
        // Currently this will just dump some string to the generated file which will prevent
        // the compilation to go through. But that may not be the best way to log errors..
        // Find out if there is any better way to log errors during source generation. 
        private void LogManifestGenerationError(string errorMessage)
        {
            //_builder.AppendLine(errorMessage);   
        }

        private class ChannelInfo
        {
            //public string? Name;
            //public ulong Keywords;
//            public EventChannelAttribute? Attribs;
        }

        private readonly Dictionary<int, string> opcodeTab;
        private Dictionary<int, string>? taskTab;
//        private Dictionary<int, ChannelInfo>? channelTab;
        private Dictionary<ulong, string>? keywordTab;
//        private Dictionary<string, Type>? mapsTab;
        private readonly Dictionary<string, string> stringTab;       // Maps unlocalized strings to localized ones

        // WCF used EventSource to mimic a existing ETW manifest.   To support this
        // in just their case, we allowed them to specify the keywords associated
        // with their channels explicitly.   ValidPredefinedChannelKeywords is
        // this set of channel keywords that we allow to be explicitly set.  You
        // can ignore these bits otherwise.
        internal const ulong ValidPredefinedChannelKeywords = 0xF000000000000000;
//        private ulong nextChannelKeywordBit = 0x8000000000000000;   // available Keyword bit to be used for next channel definition, grows down
        private const int MaxCountChannels = 8; // a manifest can defined at most 8 ETW channels

        private readonly StringBuilder sb;               // Holds the provider information.
        private readonly StringBuilder events;           // Holds the events.
        private readonly StringBuilder templates;

        private readonly string providerName;
//        private readonly ResourceManager? resources;      // Look up localized strings here.
//        private readonly EventManifestOptions flags;
        private readonly IList<string> errors;           // list of currently encountered errors
        private readonly Dictionary<string, List<int>> perEventByteArrayArgIndices;  // "event_name" -> List_of_Indices_of_Byte[]_Arg

        // State we track between StartEvent and EndEvent.
        private string? eventName;               // Name of the event currently being processed.
        private int numParams;                  // keeps track of the number of args the event has.
        private List<int>? byteArrArgIndices;   // keeps track of the index of each byte[] argument        
    }

    /// <summary>
    /// Used to send the m_rawManifest into the event dispatcher as a series of events.
    /// </summary>
    internal struct ManifestEnvelope
    {
        public const int MaxChunkSize = 0xFF00;
        public enum ManifestFormats : byte
        {
            SimpleXmlFormat = 1,          // simply dump the XML manifest as UTF8
        }

#if FEATURE_MANAGED_ETW
        public ManifestFormats Format;
        public byte MajorVersion;
        public byte MinorVersion;
        public byte Magic;
        public ushort TotalChunks;
        public ushort ChunkNumber;
#endif
    }


}
