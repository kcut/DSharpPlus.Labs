﻿using System;
using System.Buffers;
using System.Collections.Generic;

namespace Voltaic.Serialization.Json
{
    public class ArrayJsonConverter<T> : ValueConverter<T[]>
    {
        private readonly ValueConverter<T> _innerConverter;
        private readonly ArrayPool<T> _pool;

        public ArrayJsonConverter(ValueConverter<T> innerConverter, ArrayPool<T> pool = null)
        {
            _innerConverter = innerConverter;
            _pool = pool;
        }

        public override bool CanWrite(T[] value, PropertyMap propMap)
            => (!propMap.ExcludeNull && !propMap.ExcludeDefault) || value != null;

        public override bool TryRead(ref ReadOnlySpan<byte> remaining, out T[] result, PropertyMap propMap = null)
        {
            if (!JsonCollectionReader.TryRead(ref remaining, out var resultBuilder, propMap, _innerConverter, _pool))
            {
                result = default;
                return false;
            }
            if (resultBuilder.Array == null)
                result = null;
            else
                result = resultBuilder.ToArray();
            return true;
        }

        public override bool TryWrite(ref ResizableMemory<byte> writer, T[] value, PropertyMap propMap = null)
        {
            if (value == null)
                return JsonWriter.TryWriteNull(ref writer);

            writer.Push((byte)'[');
            for (int i = 0; i < value.Length; i++)
            {
                if (i != 0)
                    writer.Push((byte)',');
                if (!_innerConverter.TryWrite(ref writer, value[i], propMap))
                    return false;
            }
            writer.Push((byte)']');
            return true;
        }
    }

    public class ListJsonConverter<T> : ValueConverter<List<T>>
    {
        private readonly ValueConverter<T> _innerConverter;
        private readonly ArrayPool<T> _pool;

        public ListJsonConverter(ValueConverter<T> innerConverter, ArrayPool<T> pool = null)
        {
            _innerConverter = innerConverter;
            _pool = pool;
        }

        public override bool CanWrite(List<T> value, PropertyMap propMap)
            => (!propMap.ExcludeNull && !propMap.ExcludeDefault) || value != null;

        public override bool TryRead(ref ReadOnlySpan<byte> remaining, out List<T> result, PropertyMap propMap = null)
        {
            if (!JsonCollectionReader.TryRead(ref remaining, out var resultBuilder, propMap, _innerConverter, _pool))
            {
                result = default;
                return false;
            }
            if (resultBuilder.Array == null)
                result = null;
            else
                result = new List<T>(resultBuilder.ToArray()); // TODO: This is probably inefficient
            return true;
        }

        public override bool TryWrite(ref ResizableMemory<byte> writer, List<T> value, PropertyMap propMap = null)
        {
            if (value == null)
                return JsonWriter.TryWriteNull(ref writer);

            writer.Push((byte)'[');
            for (int i = 0; i < value.Count; i++)
            {
                if (i != 0)
                    writer.Push((byte)',');
                if (!_innerConverter.TryWrite(ref writer, value[i], propMap))
                    return false;
            }
            writer.Push((byte)']');
            return true;
        }
    }

    internal static class JsonCollectionReader
    {
        private static class EmptyArray<T>
        {
            // Array.Empty<T> wasn't added until .NET Standard 1.3
            public static readonly T[] Value = new T[0];
        }

        public static bool TryRead<T>(ref ReadOnlySpan<byte> remaining, out ResizableMemory<T> result, 
            PropertyMap propMap, ValueConverter<T> innerConverter, ArrayPool<T> pool)
        {
            result = default;
            
            switch (JsonReader.GetTokenType(ref remaining))
            {
                case JsonTokenType.StartArray:
                    break;
                case JsonTokenType.Null:
                    result = default;
                    return true;
                default:
                    return false;
            }
            remaining = remaining.Slice(1);

            if (JsonReader.GetTokenType(ref remaining) == JsonTokenType.EndArray)
            {
                result = new ResizableMemory<T>(0, pool);
                remaining = remaining.Slice(1);
                return true;
            }

            result = new ResizableMemory<T>(8, pool);
            for (int i = 0; ; i++)
            {
                switch (JsonReader.GetTokenType(ref remaining))
                {
                    case JsonTokenType.None:
                        return false;
                    case JsonTokenType.EndArray:
                        remaining = remaining.Slice(1);
                        return true;
                    case JsonTokenType.ListSeparator:
                        if (i == 0)
                            return false;
                        remaining = remaining.Slice(1);
                        break;
                    default:
                        if (i != 0)
                            return false;
                        break;
                }
                if (!innerConverter.TryRead(ref remaining, out var item, propMap))
                    return false;
                result.Push(item);
            }
        }
    }
}
