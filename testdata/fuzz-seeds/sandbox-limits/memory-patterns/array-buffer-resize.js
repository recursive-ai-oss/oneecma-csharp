const b = new ArrayBuffer(1, { maxByteLength: 2 ** 31 - 1 }); b.resize(2 ** 31 - 2);
