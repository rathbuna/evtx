﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using evtx.Tags;
using Force.Crc32;
using NLog;

namespace evtx
{
    public class ChunkInfo
    {
        public ChunkInfo(byte[] chunkBytes, long offset, int chunkNumber)
        {
            var l = LogManager.GetLogger("ChunkInfo");
            ChunkBytes = chunkBytes;
            Offset = offset;
            ChunkNumber = chunkNumber;

            FirstEventRecordNumber = BitConverter.ToInt64(chunkBytes, 0x8);
            LastEventRecordNumber = BitConverter.ToInt64(chunkBytes, 0x10);
            FirstEventRecordIdentifier = BitConverter.ToInt64(chunkBytes, 0x18);
            LastEventRecordIdentifier = BitConverter.ToInt64(chunkBytes, 0x20);

            var tableOffset = BitConverter.ToUInt32(chunkBytes, 0x28);

            LastRecordOffset = BitConverter.ToUInt32(chunkBytes, 0x2C);
            FreeSpaceOffset = BitConverter.ToUInt32(chunkBytes, 0x30);

            //TODO how to calculate this? across what data? all event records?
            var crcEventRecordsData = BitConverter.ToUInt32(chunkBytes, 0x34);

            Crc = BitConverter.ToInt32(chunkBytes, 0x7c);

            var inputArray = new byte[120 + 384 + 4];
            Buffer.BlockCopy(chunkBytes, 0, inputArray, 0, 120);
            Buffer.BlockCopy(chunkBytes, 128, inputArray, 120, 384);

            Crc32Algorithm.ComputeAndWriteToEnd(inputArray); // last 4 bytes contains CRC
            CalculatedCrc = BitConverter.ToInt32(inputArray, inputArray.Length - 4);

            var index = 0;
            var tableData = new byte[0x100];
            Buffer.BlockCopy(chunkBytes, (int) tableOffset, tableData, 0, 0x100);

            StringTableEntries = new List<StringTableEntry>();

            var stringOffsets = new List<uint>();

            while (index < tableData.Length)
            {
                var stringOffset = BitConverter.ToUInt32(tableData, index);
                index += 4;
                if (stringOffset == 0)
                {
                    continue;
                }

                stringOffsets.Add(stringOffset);
            }

            foreach (var stringOffset in stringOffsets)
            {
                index = (int) stringOffset + 4;
                var hash = BitConverter.ToUInt16(chunkBytes, index);
                index += 2;
                var stringLen = BitConverter.ToUInt16(chunkBytes, index);
                index += 2;
                var stringVal = Encoding.Unicode.GetString(chunkBytes, index, stringLen * 2);

                StringTableEntries.Add(new StringTableEntry(stringOffset, hash, stringVal));
            }

            var templateTableData = new byte[0x80];
            Buffer.BlockCopy(chunkBytes, 0x180, templateTableData, 0, 0x80);

            var tableTemplateOffsets = new List<uint>();

            index = 0;
            while (index < templateTableData.Length)
            {
                var templateOffset = BitConverter.ToUInt32(templateTableData, index);
                index += 4;
                if (templateOffset == 0)
                {
                    continue;
                }

                tableTemplateOffsets.Add(templateOffset);

                //the actual table defs live at this offset + 0x1000 for header, - 10 bytes for some reason. This is where the 0xc op code will be
                l.Trace($"Template offset: 0x {templateOffset:X}");
            }

            Templates = new Dictionary<int, Template>();

            l.Trace("\r\n----------------------NEW CHUNK--------------------------\r\n");

            //to get all the templates and cache them
            foreach (var tableTemplateOffset in tableTemplateOffsets.OrderBy(t => t))
            {
                var actualOffset = offset + tableTemplateOffset - 10; //yea, -10
                index = (int) tableTemplateOffset - 10;

                l.Trace(
                    $"Chunk offset: 0x{Offset:X} tableTemplateOffset: 0x{tableTemplateOffset:X} actualOffset: 0x {actualOffset:X} chunkBytes[index]: 0x{chunkBytes[index].ToString("X")} LastRecordOffset 0x{LastRecordOffset:X} FreeSpaceOffset 0x{FreeSpaceOffset:X}");

                var aaa = GetTemplate(index);


                if (Templates.ContainsKey(aaa.TemplateOffset) == false)
                {
                    Templates.Add(aaa.TemplateOffset, aaa);
                }

                if (aaa.NextTemplateOffset <= 0)
                {
                    continue;
                }

                var nextTemplateId = aaa.NextTemplateOffset;

                while (nextTemplateId > 0)
                {
                    var bbb = GetTemplate(nextTemplateId - 10);

                    nextTemplateId = bbb.NextTemplateOffset;

                    if (Templates.ContainsKey(bbb.TemplateOffset) == false)
                    {
                        Templates.Add(bbb.TemplateOffset, bbb);
                    }
                }
            }

            if (l.IsTraceEnabled)
            {
                foreach (var esTemplate in Templates)
                {
                    l.Trace($"key: 0x{esTemplate.Key:X} {esTemplate.Value}");
                }
            }

            index = (int) tableOffset + 0x100 + 0x80; //get to start of event Records

            EventRecords = new List<EventRecord>();

            const int recordSig = 0x2a2a;
            while (index < chunkBytes.Length)
            {
                var sig = BitConverter.ToInt32(chunkBytes, index);

                if (sig != recordSig)
                {
                    break;
                }

                var recordOffset = index;

                if (recordOffset > LastRecordOffset)
                {
                    break;
                }

                var recordSize = BitConverter.ToUInt32(chunkBytes, index + 4);
                var recordBuff = new byte[recordSize];
                Buffer.BlockCopy(chunkBytes, index, recordBuff, 0, (int) recordSize);
                index += (int) recordSize;

                var er = new EventRecord(recordBuff, recordOffset, Offset, Templates);
                EventRecords.Add(er);

            }
        }

        public uint LastRecordOffset { get; }
        public uint FreeSpaceOffset { get; }

        public Dictionary<int, Template> Templates { get; }


        public List<EventRecord> EventRecords { get; }

        public List<StringTableEntry> StringTableEntries { get; }

        public int Crc { get; }
        public int CalculatedCrc { get; }

        public long LastEventRecordIdentifier { get; }

        public long FirstEventRecordIdentifier { get; }

        public long LastEventRecordNumber { get; }

        public long FirstEventRecordNumber { get; }

        public byte[] ChunkBytes { get; }
        public long Offset { get; }
        public int ChunkNumber { get; }

        public Template GetTemplate(int startingOffset)
        {
            var index = startingOffset; //go past op code
            index += 1;

            var version = ChunkBytes[index];
            index += 1;

            var templateId = BitConverter.ToInt32(ChunkBytes, index);
            index += 4;

            var templateOffset = BitConverter.ToInt32(ChunkBytes, index);
            index += 4;

            var nextTemplateOffset = BitConverter.ToInt32(ChunkBytes, index);
            index += 4;

            var gb = new byte[16];
            Buffer.BlockCopy(ChunkBytes, index, gb, 0, 16);
            index += 16;
            var g = new Guid(gb);

            var length = BitConverter.ToInt32(ChunkBytes, index);
            index += 4;

            var templateBytes = new byte[length];
            Buffer.BlockCopy(ChunkBytes, index, templateBytes, 0, length);

            return new Template(templateId, templateOffset, g, templateBytes, nextTemplateOffset,
                Offset + startingOffset);
        }

        public override string ToString()
        {
            return
                $"RecordPosition 0x{Offset:X8} Chunk #: {ChunkNumber.ToString().PadRight(5)} FirstEventRecordNumber: {FirstEventRecordNumber.ToString().PadRight(8)} LastEventRecordNumber: {LastEventRecordNumber.ToString().PadRight(8)} FirstEventRecordIdentifier: {FirstEventRecordIdentifier.ToString().PadRight(8)} LastEventRecordIdentifier: {LastEventRecordIdentifier.ToString().PadRight(8)}";
        }
    }
}