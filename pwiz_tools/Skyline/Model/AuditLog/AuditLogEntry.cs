﻿/*
 * Original author: Tobias Rohde <tobiasr .at. uw.edu>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2018 University of Washington - Seattle, WA
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using pwiz.Common.Collections;
using pwiz.Common.SystemUtil;
using pwiz.Skyline.Model.DocSettings;
using pwiz.Skyline.Model.Serialization;
using pwiz.Skyline.Properties;
using pwiz.Skyline.Util;

namespace pwiz.Skyline.Model.AuditLog
{
    [XmlRoot(XML_ROOT)]
    public class AuditLogList : Immutable, IXmlSerializable
    {
        public const string XML_ROOT = "audit_log"; // Not L10N
        
        public AuditLogList(ImmutableList<AuditLogEntry> auditLogEntries)
        {
            AuditLogEntries = auditLogEntries;
        }

        public AuditLogList() : this(ImmutableList<AuditLogEntry>.EMPTY)
        {
        }

        public ImmutableList<AuditLogEntry> AuditLogEntries { get; private set; }

        public XmlSchema GetSchema()
        {
            return null;
        }

        public static AuditLogList Deserialize(XmlReader reader)
        {
            return reader.Deserialize(new AuditLogList());
        }

        public void ReadXml(XmlReader reader)
        {
            reader.ReadStartElement();
            var auditLogEntries = new List<AuditLogEntry>();

            while (reader.IsStartElement(AuditLogEntry.XML_ROOT))
                auditLogEntries.Add(reader.DeserializeElement<AuditLogEntry>());

            AuditLogEntries = ImmutableList.ValueOf(auditLogEntries);
            reader.ReadEndElement();
        }

        public void WriteXml(XmlWriter writer)
        {
            foreach (var entry in AuditLogEntries)
                writer.WriteElement(entry);
        }
    }

    [XmlRoot(XML_ROOT)]
    public class AuditLogEntry : Immutable, IXmlSerializable
    {
        public const string XML_ROOT = "audit_log_entry"; // Not L10N

        private IList<LogMessage> _allInfo;

        private AuditLogEntry(DocumentFormat formatVersion, DateTime timeStamp, string reason, bool insertIntoUndoRedo = false, string extraText = null)
        {
            SkylineVersion = Install.Version;
            if (Install.Is64Bit)
                SkylineVersion += " (64-Bit)"; // Not L10N

            FormatVersion = formatVersion;
            TimeStamp = timeStamp;
            ExtraText = extraText;

            using (var identity = WindowsIdentity.GetCurrent())
            {
                // ReSharper disable once PossibleNullReferenceException
                User = identity.Name;
            }

            Reason = reason ?? string.Empty;
            InsertUndoRedoIntoAllInfo = insertIntoUndoRedo;
        }


        private static PropertyName RemoveTopmostParent(PropertyName name)
        {
            if (name == PropertyName.ROOT || name.Parent == PropertyName.ROOT)
                return name;

            if (name.Parent.Parent == PropertyName.ROOT)
                return PropertyName.ROOT.SubProperty(name);

            return RemoveTopmostParent(name.Parent).SubProperty(name);
        }

        public class MessageTypeNamesPair
        {
            public MessageTypeNamesPair(MessageType type, params string[] names)
            {
                Type = type;
                Names = names;
            }

            public MessageType Type { get; private set; }
            public string[] Names { get; private set; }
        }

        // Functions to create audit log entries

        public static AuditLogEntry MakeSingleMessageEntry(DocumentFormat formatVersion, DateTime timeStamp,
            MessageTypeNamesPair typeNamesPair, string extraText = null)
        {
            var result = new AuditLogEntry(formatVersion, timeStamp, string.Empty, true, extraText);

            result.UndoRedo = new LogMessage(LogLevel.undo_redo, typeNamesPair.Type, string.Empty, false, typeNamesPair.Names);
            result.Summary = new LogMessage(LogLevel.undo_redo, typeNamesPair.Type, string.Empty, false, typeNamesPair.Names);
            result.AllInfo = new[] { typeNamesPair }
                .Select(p => new LogMessage(LogLevel.all_info, p.Type, string.Empty, false, p.Names)).ToArray();

            return result;
        }

        public static AuditLogEntry MakePropertyChangeEntry(DocumentFormat formatVersion, DiffTree tree, MessageTypeNamesPair customUndoRedo = null, string extraText = null)
        {
            var result = new AuditLogEntry(formatVersion, tree.TimeStamp, string.Empty, true, extraText);

            if (customUndoRedo == null)
            {
                var nodeNamePair = tree.Root.FindFirstMultiChildParent(tree, PropertyName.ROOT, true, false);
                // Remove "Settings" from property name if possible
                if (nodeNamePair.Name != null && nodeNamePair.Name.Parent != PropertyName.ROOT)
                {
                    var name = nodeNamePair.Name;
                    while (name.Parent.Parent != PropertyName.ROOT)
                        name = name.Parent;

                    if (name.Parent.Name == "{0:Settings}") // Not L10N
                    {
                        name = RemoveTopmostParent(nodeNamePair.Name);
                        nodeNamePair = nodeNamePair.ChangeName(name);
                    }
                }
                result.UndoRedo = nodeNamePair.ToMessage(LogLevel.undo_redo);
            }
            else
            {
                result.UndoRedo = new LogMessage(LogLevel.undo_redo, customUndoRedo.Type, string.Empty, false, customUndoRedo.Names);
            }

            result.Summary = tree.Root.FindFirstMultiChildParent(tree, PropertyName.ROOT, false, false).ToMessage(LogLevel.summary);
            result.AllInfo = tree.Root.FindAllLeafNodes(tree, PropertyName.ROOT, true)
                .Select(n => n.ToMessage(LogLevel.all_info)).ToArray();
            
            return result;
        }

        public static AuditLogEntry MakeLogEnabledDisabledEntry(DocumentFormat formatVersion, DateTime timeStamp)
        {
            var result = new AuditLogEntry(formatVersion, timeStamp, string.Empty);

            var type = Settings.Default.AuditLogging ? MessageType.log_enabled : MessageType.log_disabled;

            result.UndoRedo = new LogMessage(LogLevel.undo_redo, type, string.Empty, false);
            result.Summary = new LogMessage(LogLevel.summary, type, string.Empty, false);
            result.AllInfo = new List<LogMessage> { new LogMessage(LogLevel.all_info, type, string.Empty, false) };

            return result;
        }

        public static AuditLogEntry MakeCountEntry(MessageType type, DocumentFormat formatVersion,
            DateTime timeStamp, int undoRedoCount, int allInfoCount)
        {
            if (type != MessageType.log_unlogged_changes && type != MessageType.log_cleared)
                throw new ArgumentException();

            // ReSharper disable once UseObjectOrCollectionInitializer
            var result = new AuditLogEntry(formatVersion, timeStamp, string.Empty);

            result.UndoRedo = new LogMessage(LogLevel.undo_redo, type, string.Empty, false,
                undoRedoCount.ToString());
            result.Summary = new LogMessage(LogLevel.summary, type, string.Empty, false,
                undoRedoCount.ToString());

            result.AllInfo = new List<LogMessage>
            {
                new LogMessage(LogLevel.all_info, type, string.Empty, false,
                    allInfoCount.ToString())
            };

            result.CountEntryType = type;

            return result;
        }


        public string SkylineVersion { get; private set; }
        public DocumentFormat FormatVersion { get; private set; }
        public DateTime TimeStamp { get; private set; }
        public string User { get; private set; }
        public string Reason { get; private set; }
        public string ExtraText { get; private set; }
        public LogMessage UndoRedo { get; private set; }
        public LogMessage Summary { get; private set; }

        public IList<LogMessage> AllInfo
        {
            get { return _allInfo; }
            private set { _allInfo = ImmutableList.ValueOf(InsertUndoRedoIntoAllInfo ? new[] { UndoRedo }.Concat(value) : value); }
        }

        public bool InsertUndoRedoIntoAllInfo { get; private set; }

        public bool HasSingleAllInfoRow
        {
            get { return _allInfo.Count == 1; }
        }

        public MessageType? CountEntryType { get; private set; }

        // Property change functions

        public AuditLogEntry ChangeReason(string reason)
        {
            return ChangeProp(ImClone(this), im => im.Reason = reason);
        }

        public AuditLogEntry ChangeAllInfo(IList<LogMessage> allInfo)
        {
            return ChangeProp(ImClone(this), im => im.AllInfo = allInfo);
        }


        public void AddToDocument(SrmDocument document, Action<Func<SrmDocument, SrmDocument>> modifyDocument)
        {
            if (Settings.Default.AuditLogging || CountEntryType == MessageType.log_cleared)
            {
                modifyDocument(d => d.ChangeAuditLog(
                    ImmutableList.ValueOf(d.AuditLog.AuditLogEntries.Concat(new[] { this }))));
            }
            else
            {
                UpdateCountLogEntry(document, modifyDocument, 1, AllInfo.Count, MessageType.log_unlogged_changes);
            }
        }

        public static AuditLogEntry UpdateCountLogEntry(SrmDocument document,
            Action<Func<SrmDocument, SrmDocument>> modifyDocument, int undoRedoCount, int allInfoCount,
            MessageType type, bool addToDoc = true)
        {
            var logEntries = new List<AuditLogEntry>(document.AuditLog.AuditLogEntries);
            var countEntry = logEntries.FirstOrDefault(e => e.CountEntryType == type);

            if (countEntry != null)
            {
                var countEntries = logEntries.Where(e =>
                    e.CountEntryType == MessageType.log_cleared ||
                    e.CountEntryType == MessageType.log_unlogged_changes).ToArray();

                undoRedoCount += countEntries.Sum(e => int.Parse(e.UndoRedo.Names[0])) - countEntries.Length;
                allInfoCount += countEntries.Sum(e => int.Parse(e.AllInfo[0].Names[0])) - countEntries.Length;
                logEntries.Remove(countEntry);
            }

            var newCountEntry = MakeCountEntry(type, document.FormatVersion,
                DateTime.Now, undoRedoCount, allInfoCount);
            if (addToDoc)
            {
                logEntries.Add(newCountEntry);

                modifyDocument(d => d.ChangeAuditLog(ImmutableList<AuditLogEntry>.ValueOf(logEntries)));
            }

            return newCountEntry;
        }

        public static AuditLogEntry SettingsLogFunction(SrmDocument oldDoc, SrmDocument newDoc)
        {
            var property = new Property(typeof(SrmDocument).GetProperty("Settings"), new TrackChildrenAttribute());
            var tree = Reflector<SrmSettings>.BuildDiffTree(property, oldDoc.Settings, newDoc.Settings, DateTime.Now);
            return tree != null && tree.Root != null ? MakePropertyChangeEntry(oldDoc.FormatVersion, tree) : null;
        }

        #region Implementation of IXmlSerializable

        private AuditLogEntry()
        {
            
        }

        public XmlSchema GetSchema()
        {
            return null;
        }

        private enum ATTR
        {
            format_version,
            time_stamp,
            user,
            count_type,
            insert_undo_redo
        }

        private enum EL
        {
            message,
            reason,
            extra_text
        }

        public static AuditLogEntry Deserialize(XmlReader reader)
        {
            return reader.Deserialize(new AuditLogEntry());
        }

        public void WriteXml(XmlWriter writer)
        {
            writer.WriteAttribute(ATTR.format_version, FormatVersion.AsDouble());
            writer.WriteAttribute(ATTR.time_stamp, TimeStamp.ToUniversalTime().ToString(CultureInfo.InvariantCulture));
            writer.WriteAttribute(ATTR.user, User);

            writer.WriteAttribute(ATTR.insert_undo_redo, InsertUndoRedoIntoAllInfo);

            if (CountEntryType.HasValue)
                writer.WriteAttribute(ATTR.count_type, CountEntryType);

            if (!string.IsNullOrEmpty(Reason))
                writer.WriteElementString(EL.reason, Reason);

            if (!string.IsNullOrEmpty(ExtraText))
                writer.WriteElementString(EL.extra_text, ExtraText);
            
            writer.WriteElement(EL.message, UndoRedo);
            writer.WriteElement(EL.message, Summary);

            var startIndex = InsertUndoRedoIntoAllInfo ? 1 : 0;
            for (var i = startIndex; i < _allInfo.Count; ++i)
                writer.WriteElement(EL.message, _allInfo[i]);
        }

        public void ReadXml(XmlReader reader)
        {
            FormatVersion = new DocumentFormat(reader.GetDoubleAttribute(ATTR.format_version));
            var time = DateTime.Parse(reader.GetAttribute(ATTR.time_stamp), CultureInfo.InvariantCulture);
            TimeStamp = DateTime.SpecifyKind(time, DateTimeKind.Utc).ToLocalTime();
            User = reader.GetAttribute(ATTR.user);

            InsertUndoRedoIntoAllInfo = reader.GetBoolAttribute(ATTR.insert_undo_redo);

            var countType = reader.GetAttribute(ATTR.count_type);
            if (countType == null)
                CountEntryType = null;
            else
                CountEntryType = (MessageType) Enum.Parse(typeof(MessageType), countType);

            reader.ReadStartElement();

            Reason = reader.IsStartElement(EL.reason) ? reader.ReadElementString() : string.Empty;
            ExtraText = reader.IsStartElement(EL.extra_text) ? reader.ReadElementString() : string.Empty;

            UndoRedo = reader.DeserializeElement<LogMessage>();
            Summary = reader.DeserializeElement<LogMessage>();

            var list = new List<LogMessage>();
            while (reader.IsStartElement(EL.message))
                list.Add(reader.DeserializeElement<LogMessage>());

            AllInfo = list;

            reader.ReadEndElement();
        }
        #endregion
    }
}