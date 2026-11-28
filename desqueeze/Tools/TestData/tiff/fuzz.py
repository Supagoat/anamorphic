"""Damaged-file check for tiffref (mirror of TiffCodec): truncations and byte flips must fail cleanly, quickly."""
import os, random, sys, time, collections
import tiffref
D = sys.argv[1]
files = sorted(f for f in os.listdir(D) if f.startswith('test-') and os.path.getsize(os.path.join(D, f)) < 20000)
rng = random.Random(7)
kinds = collections.Counter()
slow = []
for f in files:
    data = open(os.path.join(D, f), 'rb').read()
    variants = [data[:int(len(data) * k / 20)] for k in range(20)]
    for _ in range(40):
        b = bytearray(data)
        for _ in range(rng.randint(1, 4)):
            b[rng.randrange(len(b))] = rng.randrange(256)
        variants.append(bytes(b))
    for v in variants:
        t = time.time()
        try:
            w, h, ch, depth, s, meta = tiffref.Decode(v)
            kinds['decoded'] += 1
            if w * h * ch > 50_000_000:
                slow.append((f, 'huge', w, h, ch))
        except (ValueError, tiffref.NotSupported) as e:
            kinds['clean'] += 1
        except Exception as e:
            kinds[type(e).__name__] += 1
        if time.time() - t > 2:
            slow.append((f, time.time() - t))
print(dict(kinds))
print('slow/huge:', slow[:10])
