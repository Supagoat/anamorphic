"""
A line-for-line Python translation of TiffCodec.cs (decode, encode, EXIF handling), to check its algorithms against
Pillow/libtiff here, where no C# compiler is available. Function and variable names follow the C#.
Images are returned as (width, height, channels, depth, flat list of samples, metadata dict).
"""
import struct
import zlib

TagNewSubfileType, TagImageWidth, TagImageLength, TagBitsPerSample, TagCompression = 254, 256, 257, 258, 259
TagPhotometric, TagStripOffsets, TagOrientation, TagSamplesPerPixel, TagRowsPerStrip = 262, 273, 274, 277, 278
TagStripByteCounts, TagXResolution, TagYResolution, TagPlanarConfig, TagResolutionUnit = 279, 282, 283, 284, 296
TagPredictor, TagColorMap, TagTileWidth, TagTileLength, TagTileOffsets, TagTileByteCounts = 317, 320, 322, 323, 324, 325
TagExtraSamples, TagSampleFormat, TagIptc, TagExifIfd, TagIccProfile, TagGpsIfd, TagInteropIfd = 338, 339, 33723, 34665, 34675, 34853, 40965
TypeByte, TypeAscii, TypeShort, TypeLong, TypeRational, TypeSByte, TypeUndefined, TypeSShort, TypeSLong, TypeSRational, TypeFloat, TypeDouble, TypeIfd = range(1, 14)

DescriptiveTags = {269, 270, 271, 272, TagOrientation, TagXResolution, TagYResolution, TagResolutionUnit, 305, 306, 315, 316, 33432,
                   TagIptc, TagExifIfd, TagGpsIfd}


def U16(b, p, le):
    return b[p] | (b[p + 1] << 8) if le else (b[p] << 8) | b[p + 1]


def U32(b, p, le):
    return (b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)) if le else ((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3])


def W16(b, p, v):
    b[p] = v & 0xFF
    b[p + 1] = (v >> 8) & 0xFF


def W32(b, p, v):
    for k in range(4):
        b[p + k] = (v >> (8 * k)) & 0xFF


def IsTiff(d):
    if d is None or len(d) < 8:
        return False
    if d[0] == ord('I') and d[1] == ord('I'):
        return d[2] in (42, 43) and d[3] == 0
    if d[0] == ord('M') and d[1] == ord('M'):
        return d[2] == 0 and d[3] in (42, 43)
    return False


def TypeSize(t):
    return {1: 1, 2: 1, 6: 1, 7: 1, 3: 2, 8: 2, 4: 4, 9: 4, 11: 4, 13: 4, 5: 8, 10: 8, 12: 8}.get(t, 0)


def SwapUnit(t):
    return {3: 2, 8: 2, 4: 4, 9: 4, 11: 4, 13: 4, 5: 4, 10: 4, 12: 8}.get(t, 1)


class Entry:
    def __init__(self, Type, Count, Value, Le):
        self.Type, self.Count, self.Value, self.Le = Type, Count, Value, Le


class Ifd:
    def __init__(self):
        self.Entries = {}
        self.Next = 0


def ReadIfd(d, offset, le):
    if offset < 8 or offset + 2 > len(d):
        raise ValueError("directory outside the file")
    p = offset
    n = U16(d, p, le)
    if p + 2 + 12 * n > len(d):
        raise ValueError("directory truncated")
    ifd = Ifd()
    for i in range(n):
        e = p + 2 + 12 * i
        tag, typ = U16(d, e, le), U16(d, e + 2, le)
        count = U32(d, e + 4, le)
        size = TypeSize(typ)
        if size == 0:
            continue
        nbytes = count * size
        if nbytes <= 4:
            v = bytes(d[e + 8:e + 8 + nbytes])
        else:
            at = U32(d, e + 8, le)
            if at + nbytes > len(d):
                continue
            v = bytes(d[at:at + nbytes])
        ifd.Entries[tag] = Entry(typ, count, v, le)
    nextAt = p + 2 + 12 * n
    ifd.Next = U32(d, nextAt, le) if nextAt + 4 <= len(d) else 0
    return ifd


def IntE(e, i):
    if e.Type in (TypeByte, TypeUndefined):
        return e.Value[i]
    if e.Type == TypeSByte:
        return struct.unpack('b', e.Value[i:i + 1])[0]
    if e.Type == TypeShort:
        return U16(e.Value, 2 * i, e.Le)
    if e.Type == TypeSShort:
        v = U16(e.Value, 2 * i, e.Le)
        return v - 65536 if v >= 32768 else v
    if e.Type in (TypeLong, TypeIfd):
        return U32(e.Value, 4 * i, e.Le)
    if e.Type == TypeSLong:
        v = U32(e.Value, 4 * i, e.Le)
        return v - (1 << 32) if v >= 1 << 31 else v
    raise ValueError("not an integer field")


def Ints(ifd, tag):
    e = ifd.Entries.get(tag)
    if e is None or e.Count <= 0:
        return None
    return [IntE(e, i) for i in range(e.Count)]


def Int(ifd, tag, fallback):
    e = ifd.Entries.get(tag)
    if e is None or e.Count <= 0:
        return fallback
    return IntE(e, 0)


def Required(ifd, tag, name):
    e = ifd.Entries.get(tag)
    if e is None or e.Count <= 0:
        raise ValueError("no " + name)
    return IntE(e, 0)


class NotSupported(Exception):
    pass


def Decode(data):
    if not IsTiff(data):
        raise ValueError("Not a TIFF file.")
    le = data[0] == ord('I')
    if U16(data, 2, le) == 43:
        raise NotSupported("BigTIFF")
    main = first = None
    off = U32(data, 4, le)
    seen = set()
    while off != 0 and off not in seen and len(seen) < 64:
        seen.add(off)
        ifd = ReadIfd(data, off, le)
        if first is None:
            first = ifd
        if (Int(ifd, TagNewSubfileType, 0) & 1) == 0:
            main = ifd
            break
        off = ifd.Next
    m = main or first
    width, height = Required(m, TagImageWidth, "width"), Required(m, TagImageLength, "height")
    spp = Int(m, TagSamplesPerPixel, 1)
    bpsAll = Ints(m, TagBitsPerSample)
    bits = bpsAll[0] if bpsAll else 1
    if bpsAll:
        for i in range(1, min(len(bpsAll), spp)):
            if bpsAll[i] != bits:
                raise NotSupported("mixed depths")
    if bits not in (8, 16):
        raise NotSupported("%d-bit" % bits)
    formats = Ints(m, TagSampleFormat)
    if formats and formats[0] != 1:
        raise NotSupported("sample format")
    compression = Int(m, TagCompression, 1)
    if compression not in (1, 5, 8, 32946, 32773):
        raise NotSupported("compression %d" % compression)
    photometric = Int(m, TagPhotometric, 2 if spp >= 3 else 1)
    if photometric in (0, 1, 3):
        colorSamples = 1
    elif photometric == 2:
        colorSamples = 3
    else:
        raise NotSupported("photometric %d" % photometric)
    palette = photometric == 3
    if palette and bits != 8:
        raise NotSupported("16-bit palette")
    planar = Int(m, TagPlanarConfig, 1)
    predictor = Int(m, TagPredictor, 1)
    if predictor not in (1, 2):
        raise NotSupported("predictor")
    hasAlpha = spp > colorSamples
    extras = Ints(m, TagExtraSamples)
    associated = hasAlpha and extras is not None and extras[0] == 1
    outCh = (3 if palette else colorSamples) + (1 if hasAlpha else 0)
    palR = palG = palB = None
    if palette:
        cm = Ints(m, TagColorMap)
        if cm is None or len(cm) < 768:
            raise ValueError("no colour map")
        palR = [cm[i] >> 8 for i in range(256)]
        palG = [cm[256 + i] >> 8 for i in range(256)]
        palB = [cm[512 + i] >> 8 for i in range(256)]
    tiled = TagTileWidth in m.Entries
    if tiled:
        tw, th = Required(m, TagTileWidth, "tw"), Required(m, TagTileLength, "th")
        offsets, counts = Ints(m, TagTileOffsets), Ints(m, TagTileByteCounts)
    else:
        tw = width
        th = min(Int(m, TagRowsPerStrip, 0xFFFFFFFF), height)
        offsets, counts = Ints(m, TagStripOffsets), Ints(m, TagStripByteCounts)
    if tw <= 0 or th <= 0 or tw > 1 << 20 or th > 1 << 20:
        raise ValueError("impossible strip or tile size")
    if offsets is None:
        raise ValueError("no image data")
    across, down = (width + tw - 1) // tw, (height + th - 1) // th
    planes = spp if planar == 2 else 1
    blockSpp = 1 if planar == 2 else spp
    blocks = across * down * planes
    if len(offsets) < blocks:
        raise ValueError("too few strips")
    bytesPerSample = bits // 8
    outDepth = 16 if bits == 16 and not palette else 8
    dst = [0] * (width * height * outCh)

    for b in range(blocks):
        plane = b // (across * down)
        r = b % (across * down)
        ty, tx = r // across, r % across
        x0, y0 = tx * tw, ty * th
        rows = th if tiled else min(th, height - y0)
        cols = tw
        rowSamples = cols * blockSpp
        expected = rows * rowSamples * bytesPerSample
        start = offsets[b]
        avail = counts[b] if counts is not None and b < len(counts) else expected
        if start + avail > len(data):
            avail = len(data) - start
        if avail < 0:
            avail = 0
        raw = Unpack(data, start, avail, expected, compression)
        mp = []
        for s in range(blockSpp):
            fs = plane if planar == 2 else s
            if palette:
                mp.append(-2 if fs == 0 else (3 if hasAlpha and fs == 1 else -1))
            elif fs < colorSamples:
                mp.append(fs)
            else:
                mp.append(colorSamples if hasAlpha and fs == colorSamples else -1)
        if bits == 8:
            vals = raw
        else:
            n = rows * rowSamples
            vals = [(raw[2 * i] | (raw[2 * i + 1] << 8)) if le else ((raw[2 * i] << 8) | raw[2 * i + 1]) for i in range(n)]
        if predictor == 2:
            mask = 0xFF if bits == 8 else 0xFFFF
            for y in range(rows):
                o = y * rowSamples
                for i in range(blockSpp, rowSamples):
                    vals[o + i] = (vals[o + i] + vals[o + i - blockSpp]) & mask
        for y in range(rows):
            iy = y0 + y
            if iy >= height:
                break
            srcRow = y * rowSamples
            for x in range(cols):
                ix = x0 + x
                if ix >= width:
                    break
                si, di = srcRow + x * blockSpp, (iy * width + ix) * outCh
                for s in range(blockSpp):
                    c = mp[s]
                    v = vals[si + s]
                    if c >= 0:
                        dst[di + c] = v
                    elif c == -2:
                        dst[di], dst[di + 1], dst[di + 2] = palR[v], palG[v], palB[v]
    mx = 255 if outDepth == 8 else 65535
    if photometric == 0:
        for i in range(width * height):
            dst[i * outCh] = mx - dst[i * outCh]
    if associated:
        cc = outCh - 1
        for i in range(width * height):
            o = i * outCh
            a = dst[o + cc]
            for c in range(cc):
                v = dst[o + c]
                dst[o + c] = 0 if a == 0 else min(mx, (v * mx + a // 2) // a)
    meta = {}
    icc = m.Entries.get(TagIccProfile)
    if icc is not None and len(icc.Value) > 0:
        meta['icc'] = icc.Value
    try:
        meta['exif'] = BuildExif(data, le, m)
    except Exception:
        meta['exif'] = None
    return width, height, outCh, outDepth, dst, meta


def Unpack(d, start, count, expected, compression):
    r = bytearray(expected)
    if compression == 1:
        n = min(count, expected)
        r[0:n] = d[start:start + n]
    elif compression == 5:
        LzwDecode(d, start, count, r)
    elif compression in (8, 32946):
        z = zlib.decompress(bytes(d[start:start + count]))
        n = min(len(z), expected)
        r[0:n] = z[0:n]
    elif compression == 32773:
        PackBitsDecode(d, start, count, r)
    return r


def LzwDecode(src, off, ln, dst):
    Clear, Eoi = 256, 257
    if ln >= 2 and src[off] == 0 and (src[off + 1] & 1) != 0:
        raise NotSupported("old LZW")
    prefix = [-1] * 4096
    suffix = [0] * 4096
    firstByte = [0] * 4096
    length = [0] * 4096
    for i in range(256):
        prefix[i], suffix[i], firstByte[i], length[i] = -1, i, i, 1
    width, nxt, prev, outPos = 9, 258, -1, 0
    bitBuf = bitCount = 0
    p, end = off, off + ln
    while outPos < len(dst):
        while bitCount < width:
            if p >= end:
                return
            bitBuf = (bitBuf << 8) | src[p]
            p += 1
            bitCount += 8
        code = (bitBuf >> (bitCount - width)) & ((1 << width) - 1)
        bitCount -= width
        bitBuf &= (1 << bitCount) - 1
        if code == Eoi:
            return
        if code == Clear:
            width, nxt, prev = 9, 258, -1
            continue
        if prev < 0:
            if code > 255:
                raise ValueError("damaged")
            dst[outPos] = code
            outPos += 1
            prev = code
            continue
        if code < nxt:
            st = code
        elif code == nxt:
            st = -1
        else:
            raise ValueError("damaged")
        if nxt < 4096:
            prefix[nxt] = prev
            suffix[nxt] = firstByte[st] if st >= 0 else firstByte[prev]
            firstByte[nxt] = firstByte[prev]
            length[nxt] = length[prev] + 1
            nxt += 1
            if nxt >= (1 << width) - 1 and width < 12:
                width += 1
        if st < 0:
            st = nxt - 1
        l = length[st]
        pos = outPos + l - 1
        c = st
        while c >= 0:
            if pos < len(dst):
                dst[pos] = suffix[c]
            c = prefix[c]
            pos -= 1
        outPos += l
        prev = code


def PackBitsDecode(src, off, ln, dst):
    p, end, o = off, off + ln, 0
    while o < len(dst) and p < end:
        n = struct.unpack('b', bytes([src[p]]))[0]
        p += 1
        if n >= 0:
            c = min(n + 1, min(end - p, len(dst) - o))
            dst[o:o + c] = src[p:p + c]
            p += n + 1
            o += c
        elif n != -128:
            if p >= end:
                break
            v = src[p]
            p += 1
            c = min(1 - n, len(dst) - o)
            for i in range(c):
                dst[o + i] = v
            o += c


# ------------------------------------------------------------------ metadata

class OutIfd:
    def __init__(self):
        self.Entries = {}
        self.Children = {}

    def Empty(self):
        return not self.Entries and not self.Children


MaxExifBytes = 65000


def IsSubIfd(tag):
    return tag in (TagExifIfd, TagGpsIfd, TagInteropIfd)


def Copy(data, le, src, keep, depth):
    o = OutIfd()
    for tag, e in src.Entries.items():
        if keep is not None and tag not in keep:
            continue
        if IsSubIfd(tag):
            if depth >= 3 or e.Count < 1 or e.Type not in (TypeLong, TypeIfd):
                continue
            try:
                child = Copy(data, le, ReadIfd(data, IntE(e, 0), le), None, depth + 1)
                if not child.Empty():
                    o.Children[tag] = child
            except Exception:
                pass
            continue
        o.Entries[tag] = LittleEndian(tag, e)
    return o


def LittleEndian(tag, e):
    if tag == TagIptc:
        return Entry(TypeUndefined, len(e.Value), bytes(e.Value), True)
    v = bytearray(e.Value)
    unit = SwapUnit(e.Type)
    if not e.Le and unit > 1:
        i = 0
        while i + unit <= len(v):
            v[i:i + unit] = v[i:i + unit][::-1]
            i += unit
    return Entry(e.Type, e.Count, bytes(v), True)


def BuildExif(data, le, image):
    root = Copy(data, le, image, DescriptiveTags, 0)
    if root.Empty():
        return None
    ms = bytearray(b'II*\x00\x08\x00\x00\x00')
    WriteIfd(ms, root)
    if len(ms) > MaxExifBytes and TagIptc in root.Entries:
        del root.Entries[TagIptc]
        if root.Empty():
            return None
        ms = bytearray(b'II*\x00\x08\x00\x00\x00')
        WriteIfd(ms, root)
    return bytes(ms)


def FromExif(exif):
    try:
        if exif is None or len(exif) < 8:
            return None
        if exif[0:2] == b'II':
            le = True
        elif exif[0:2] == b'MM':
            le = False
        else:
            return None
        if U16(exif, 2, le) != 42:
            return None
        return Copy(exif, le, ReadIfd(exif, U32(exif, 4, le), le), DescriptiveTags, 0)
    except Exception:
        return None


def WriteIfd(ms, ifd):
    if len(ms) & 1:
        ms.append(0)
    tags = list(ifd.Entries.keys()) + [k for k in ifd.Children if k not in ifd.Entries]
    tags.sort()
    n = len(tags)
    at = len(ms)
    dr = bytearray(2 + 12 * n + 4)
    W16(dr, 0, n)
    ms.extend(dr)
    slots = []
    for i, tag in enumerate(tags):
        e = 2 + 12 * i
        W16(dr, e, tag)
        if tag in ifd.Children:
            W16(dr, e + 2, TypeLong)
            W32(dr, e + 4, 1)
            slots.append((tag, e + 8))
            continue
        en = ifd.Entries[tag]
        W16(dr, e + 2, en.Type)
        W32(dr, e + 4, en.Count)
        if len(en.Value) <= 4:
            dr[e + 8:e + 8 + len(en.Value)] = en.Value
        else:
            if len(ms) & 1:
                ms.append(0)
            W32(dr, e + 8, len(ms))
            ms.extend(en.Value)
    for tag, slot in slots:
        c = WriteIfd(ms, ifd.Children[tag])
        W32(dr, slot, c)
    ms[at:at + len(dr)] = dr
    return at


def Shorts(*v):
    b = bytearray(2 * len(v))
    for i, x in enumerate(v):
        W16(b, 2 * i, x)
    return Entry(TypeShort, len(v), bytes(b), True)


def Longs(*v):
    b = bytearray(4 * len(v))
    for i, x in enumerate(v):
        W32(b, 4 * i, x)
    return Entry(TypeLong, len(v), bytes(b), True)


def Rational(num, den):
    b = bytearray(8)
    W32(b, 0, num)
    W32(b, 4, den)
    return Entry(TypeRational, 1, bytes(b), True)


def Encode(w, h, ch, bits, samples, meta, deflate=True, strip_bytes=1 << 20):
    rowSamples = w * ch
    rowBytes = rowSamples * (bits // 8)
    rowsPerStrip = max(1, min(h, strip_bytes // max(1, rowBytes)))
    strips = (h + rowsPerStrip - 1) // rowsPerStrip
    stripData = []
    for s in range(strips):
        y0, rows = s * rowsPerStrip, min(rowsPerStrip, h - (s * rowsPerStrip))
        raw = bytearray(rows * rowBytes)
        if bits == 8:
            raw[:] = bytes(samples[y0 * rowSamples:(y0 + rows) * rowSamples])
            if deflate:
                for y in range(rows):
                    o = y * rowSamples
                    for i in range(rowSamples - 1, ch - 1, -1):
                        raw[o + i] = (raw[o + i] - raw[o + i - ch]) & 0xFF
        else:
            for y in range(rows):
                si, o = (y0 + y) * rowSamples, y * rowSamples * 2
                for i in range(rowSamples):
                    v = samples[si + i]
                    if deflate and i >= ch:
                        v = (v - samples[si + i - ch]) & 0xFFFF
                    raw[o + 2 * i] = v & 0xFF
                    raw[o + 2 * i + 1] = v >> 8
        stripData.append(zlib.compress(bytes(raw), 9) if deflate else bytes(raw))
    ms = bytearray(b'II*\x00\x00\x00\x00\x00')
    stripOffsets, stripCounts = [], []
    for sd in stripData:
        if len(ms) & 1:
            ms.append(0)
        stripOffsets.append(len(ms))
        stripCounts.append(len(sd))
        ms.extend(sd)
    root = (FromExif(meta.get('exif')) if meta else None) or OutIfd()
    root.Entries[TagNewSubfileType] = Longs(0)
    root.Entries[TagImageWidth] = Longs(w)
    root.Entries[TagImageLength] = Longs(h)
    root.Entries[TagBitsPerSample] = Shorts(*([bits] * ch))
    root.Entries[TagCompression] = Shorts(8 if deflate else 1)
    root.Entries[TagPhotometric] = Shorts(1 if ch <= 2 else 2)
    root.Entries[TagStripOffsets] = Longs(*stripOffsets)
    root.Entries[TagOrientation] = Shorts(1)
    root.Entries[TagSamplesPerPixel] = Shorts(ch)
    root.Entries[TagRowsPerStrip] = Longs(rowsPerStrip)
    root.Entries[TagStripByteCounts] = Longs(*stripCounts)
    if TagXResolution not in root.Entries or TagYResolution not in root.Entries:
        root.Entries[TagXResolution] = Rational(72, 1)
        root.Entries[TagYResolution] = Rational(72, 1)
        root.Entries[TagResolutionUnit] = Shorts(2)
    root.Entries[TagPlanarConfig] = Shorts(1)
    if deflate:
        root.Entries[TagPredictor] = Shorts(2)
    if ch in (2, 4):
        root.Entries[TagExtraSamples] = Shorts(2)
    root.Entries[TagSampleFormat] = Shorts(*([1] * ch))
    if meta and meta.get('icc'):
        root.Entries[TagIccProfile] = Entry(TypeUndefined, len(meta['icc']), meta['icc'], True)
    ifd = WriteIfd(ms, root)
    W32(ms, 4, ifd)
    return bytes(ms)
