"""Export a learned filter bank (+ optional MLP) to the binary format read by Desqueeze.Core (AxisSRModel).

    python3 export_model.py bank.pkl net.pkl|- out.bin [name]

Format (little-endian):
    "DSQM", int version (1), int nameLen, utf-8 name
    int rows, cols, P (phases), Q (angles), S (strengths), C (coherences), E (must be 1)
    float sigma (structure tensor), int space (0 gamma, 1 linear, 2 sigmoid), int reserved
    float scaleMin, scaleMax (range the model was trained for)
    3 x (int n, float[n]): strength edges, coherence edges, edge-offset edges (unused, n = 0)
    int n, float[n] filters, ordered [P][Q][S][C][E][rows*cols]
    int layers; if > 0: float leak, int hasScaleInput, then per layer int in, int out, float W[in][out], float bias[out]
"""
import os, sys, struct, pickle, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

SPACES = {"gamma": 0, "linear": 1, "sigmoid": 2}

def export(bank_pkl, mlp_pkl, out_path, name="", scale_min=1.15, scale_max=2.0):
    bank = pickle.load(open(bank_pkl, "rb"))
    cfg, filt = bank["cfg"], bank["filt"]
    net = pickle.load(open(mlp_pkl, "rb")) if mlp_pkl else None
    E = cfg.E
    b = bytearray()
    b += b"DSQM"
    b += struct.pack("<i", 1)                                   # format version
    nb = name.encode("utf-8")
    b += struct.pack("<i", len(nb)) + nb
    b += struct.pack("<iiiiiii", cfg.rows, cfg.cols, cfg.P, cfg.Q, cfg.S, cfg.C, E)
    b += struct.pack("<fii", cfg.sigma, SPACES[cfg.space], 0)
    b += struct.pack("<ff", scale_min, scale_max)
    for arr in (cfg.s_edges, cfg.c_edges, cfg.E_edges if E > 1 else np.zeros(0)):
        arr = np.asarray(arr, np.float32)
        b += struct.pack("<i", len(arr)) + arr.tobytes()
    f = np.asarray(filt, np.float32).reshape(-1)
    assert f.size == cfg.P * cfg.Q * cfg.S * cfg.C * E * cfg.NT
    b += struct.pack("<i", f.size) + f.tobytes()
    if net is None:
        b += struct.pack("<i", 0)
    else:
        b += struct.pack("<i", len(net.W))
        b += struct.pack("<f", 0.05)                             # leaky ReLU slope
        b += struct.pack("<i", 1 if net.W[0].shape[0] == cfg.NT + 3 else 0)   # has scale input
        for W, bias in zip(net.W, net.b):
            W = np.asarray(W, np.float32); bias = np.asarray(bias, np.float32)
            b += struct.pack("<ii", W.shape[0], W.shape[1]) + W.tobytes() + bias.tobytes()
    open(out_path, "wb").write(bytes(b))
    print(f"wrote {out_path}: {len(b)/1024:.0f} KB, buckets {cfg.nbuckets}, taps {cfg.NT}, mlp {[w.shape for w in net.W] if net else None}")

if __name__ == "__main__":
    export(sys.argv[1], sys.argv[2] if sys.argv[2] != "-" else None, sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else "")
