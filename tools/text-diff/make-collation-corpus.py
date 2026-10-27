#!/usr/bin/env python3
"""Writes testdata/text/collation-corpus.json: the strings both collators order.

The strings are our own: words and fragments in the six supported languages, punctuation and spacing
variants, digits and numbers, compatibility characters, combining sequences, kana and a few symbols.
Han ideographs are left out on purpose (the root order uses radical-stroke order, which this
implementation does not carry). Regenerate with: python3 tools/text-diff/make-collation-corpus.py
"""
import json
import os

S = []


def add(*items):
    for x in items:
        if x not in S:
            S.append(x)


# case and accents
add('a', 'A', '\u00e1', '\u00c1', '\u00e0', '\u00e2', '\u00e4', '\u00e5', '\u00c5', '\u212b', 'a\u030a', 'A\u030a',
    'b', 'B', 'c', 'C', '\u00e7', '\u00c7', 'd', 'e', 'E', '\u00e9', '\u00c9', '\u00e8', '\u00ea', '\u00eb', 'e\u0301',
    'f', 'g', 'h', 'i', 'I', '\u00ed', '\u00ef', 'j', 'k', 'l', 'L', 'm', 'n', 'N', '\u00f1', '\u00d1', 'n\u0303',
    'o', 'O', '\u00f3', '\u00f2', '\u00f4', '\u00f6', '\u00d6', '\u00f8', '\u0153', '\u0152', 'p', 'q', 'r', 's', 'S',
    '\u00df', '\u1e9e', 'ss', 'SS', 't', 'u', 'U', '\u00fa', '\u00fc', '\u00dc', 'v', 'w', 'x', 'y', '\u00fd', '\u00ff',
    'z', 'Z', '\u00e6', '\u00c6', 'ae', 'AE', 'oe', 'OE', 'ue', 'UE', '\u00fe', '\u00f0')
# Spanish
add('ca', 'cb', 'cc', 'ch', 'cha', 'chz', 'Ch', 'CH', 'cH', 'ci', 'cz', 'da', 'la', 'll', 'lla', 'llz', 'Ll', 'LL',
    'lz', 'lm', 'ma', 'mz', 'na', 'n\u00f1', '\u00f1a', '\u00d1a', '\u00f1u', 'nz', 'oa', 'o')
# German
add('Ab', '\u00c4b', 'Aeb', '\u00e4b', 'aeb', 'Zebra', 'Z\u00fcrich', 'Zuerich', 'Zurich', 'M\u00fcller', 'Mueller',
    'Muller', 'M\u00f6ller', 'Moeller', 'Stra\u00dfe', 'Strasse', 'Strase', 'Stra\u00dfen', 'Gr\u00fc\u00dfe', 'Gruesse',
    'Gr\u00fcne', 'Gruene', 'gross', 'gro\u00df')
# French
add('cote', 'cot\u00e9', 'c\u00f4te', 'c\u00f4t\u00e9', 'coter', 'c\u00f4ter', 'coup', 'co\u00fbt', 'cout',
    'r\u00e9sum\u00e9', 'resume', 'r\u00e9sume', 'resum\u00e9', 'p\u00eache', 'p\u00e9ch\u00e9', 'peche', 'p\u00e8che',
    '\u00e9t\u00e9', 'ete', '\u00eatre', '\u0153uvre', 'oeuvre', 'ma\u00eetre', 'naif', 'na\u00eff', 'na\u00efve')
# Portuguese
add('a\u00e7\u00e3o', 'acao', 'a\u00e7\u00f5es', 'a\u00e7ao', 'ac\u00e3o', '\u00e3o', 'ao', 'ol\u00e1', 'ola',
    'vov\u00f3', 'vov\u00f4', 'vovo', 'cora\u00e7\u00e3o', 'coracao')
# punctuation and spacing
add('', 'a b', 'ab', 'a-b', 'a_b', 'a.b', 'a,b', 'a b ', 'a  b', ' ab', '-ab', '_ab', '(ab)', 'ab)', '[ab]',
    'a\u2010b', 'a\u2013b', 'a\u2014b', 'a\u2019b', "a'b", 'a"b', 'a/b', 'a\\b', 'a&b', 'a@b', 'a#b', 'a$b', 'a%b',
    'a*b', 'a+b', 'a=b', 'a~b', 'a|b', 'a^b', 'a`b', 'a\u00a0b', 'a\tb', 'a\nb', '$a', '\u20aca', '\u00a3a', '+a',
    '=a', '<a', '>a', '!', '?', '.', ',', ';', ':', "'", '"', '(', ')', '-', '_', ' ', '*', '&', '@', '#', '%', '/',
    '\\', '~', '$', '\u20ac', '\u00a3', '\u00a5', '+', '=', '<', '>', '^', '`', '|', '\u00a9', '\u00ae', '\u2122',
    '\u00b0', '\u00a7', '\u00b6', '\u2022', '\u2026', '\u00bf', '\u00a1', '\u00ab', '\u00bb')
# digits and numbers
add('0', '1', '2', '3', '9', '10', '11', '100', '01', '001', '1a', '1b', 'a1', 'a2', 'a10', 'a9', 'a02', 'a002',
    'a2b', 'a10b', 'a2c', 'a1.5', 'a1.10', 'a1,5', 'item 1', 'item 2', 'item 10', 'item 02', 'file1.txt',
    'file2.txt', 'file10.txt', 'file01.txt', 'v1.2.3', 'v1.10.0', 'v1.9.9', '007', '7', '\u0663', '\u0661\u0660',
    '\u0967', '\u0968', '\uff11', '\uff12', '\u00b2', '\u00b3', '\u00bd', '\u00bc', 'x\u00b2', 'x2', '\u2460',
    '\u2461', '12345678901234567890', '12345678901234567891', 'abc123', 'abc1234', 'abc12')
# ligatures and compatibility characters
add('\ufb01', 'fi', '\ufb02', 'fl', '\u0132', 'IJ', 'ij', '\u0133', '\u01c4', 'DZ', '\u2160', 'II', '\u2161',
    '\u3392', 'MHz', '\u2126', '\u03a9', '\u00b5', '\u03bc', '\u0394')
# Greek, Cyrillic, Hebrew, Arabic
add('\u03b1', '\u0391', '\u03ac', '\u03b2', '\u03b3', '\u03c9', '\u0430', '\u0410', '\u0431', '\u0432', '\u044f',
    '\u0451', '\u0435', '\u0436', '\u05d0', '\u05d1', '\u05ea', '\u0627', '\u0628', '\u064a')
# kana
add('\u3042', '\u30a2', '\u3041', '\u30a1', '\u3044', '\u30a4', '\u304b', '\u30ab', '\u304c', '\u30ac', '\u3093',
    '\u30f3', '\uff71', '\u30fc', '\u30a2\u30fc')
# symbols and emoji
add('\U0001f600', '\U0001f601', '\u2764', '\u2605', '\u2606', '\u266a', '\u2660')
# combining marks, controls and ignorables
add('e\u0301\u0323', 'e\u0323\u0301', '\u1eb9\u0301', '\u00e9\u0323', 'e\u0302', '\u00ea', 'a\u0308',
    'a\u0308\u0304', '\u00e4\u0304', 'a\u0304\u0308', '\u0101\u0308', 'o\u0308\u0301', 'n\u0303\u0301',
    '\u00f1\u0301', 'n\u0301\u0303', '\u0301', '\u0301a', 'a\u0301b', 'ab\u0301', 'a\u0000b', 'a\u00adb',
    'a\u200bb', 'a\u2060b', '\u200b')
# words and names
add('apple', 'Apple', 'APPLE', 'apple pie', 'applepie', 'Apple Pie', 'apple-pie', 'apple_pie', 'banana', 'Banana',
    'cherry', 'Cherry', 'cherry tree', 'cherrytree', 'cherry-tree', 'zebra', '\u00c4pfel', 'Apfel', 'apfel',
    '\u00e4pfel', 'Aepfel', 'Zoo', 'zoo', 'z\u00f4o', 'John Smith', 'john smith', 'Smith, John', 'smith john',
    "O'Brien", 'OBrien', 'O Brien', 'O-Brien', 'McDonald', 'Mc Donald', 'Mac Donald', 'de la Cruz', 'de-la-Cruz',
    'dela Cruz', 'delacruz', 'van der Berg', 'Vanderberg', '\u00d3scar', 'Oscar', '\u00f3scar', '\u00d6stlich',
    'Ostlich', 'Oestlich', '\u00c5ngstr\u00f6m', 'Angstrom', 'Angstroem', '\u00e5ngstr\u00f6m', 'Ni\u00f1o', 'Nino',
    'ni\u00f1o', 'Pi\u00f1a', 'Pina', 'pi\u00f1a', 'pi\u00f1o', 'pino', 'Se\u00f1or', 'senor', 'se\u00f1or',
    'se\u00f1ora', 'ma\u00f1ana', 'manana', 'Mallorca', 'Mal\u00f6rca', 'llama', 'lluvia', 'luna', 'chico', 'Chile',
    'cielo', 'ciudad', 'cuenta', 'Cu\u00e9ntame', 'cuentame', 'caf\u00e9', 'cafe', 'Caf\u00e9', 'CAF\u00c9',
    'cafe\u0301', 'caf\u00e9s', 'cafes', 'caf\u00e9 au lait', 'cafe au lait', 'hello', 'Hello', 'HELLO',
    'hello world', 'hello, world', 'hello-world', 'hello_world', 'hello.world', 'hello  world', 'h\u00e9llo',
    'hell\u00f6', 'h\u00ebllo')


# other scripts, contractions, discontiguous marks and supplementary characters
add('한', '한국어', '한', '가', '가', '각', '나', '다', '힣',
    'अ', 'आ', 'क', 'क्ष', 'ख', 'ก', 'ข', 'เก', 'กเ',
    'ཀ', 'ཀྵ', 'ཱི', 'ཱི', 'ა', 'ბ', 'Ա', 'ա', 'ሀ', 'Ꭰ',
    '𐐀', '𐐨', '𝟎', '𝟏', 'ⅠⅡ', 'க', 'க்', '்',
    'ab̀́', 'ab́̀', 'ạ̇', 'ạ̇', 'ạ̇', 'ạ̇',
    'q̣̇', 'Ạ̇', 'Ź', 'Ż́', 'ff', 'ﬀ', 'ffi', 'ﬃ', 'st', 'ﬆ',
    '½', '1/2', '1⁄2', '¾', '⅓', '↉', '⅐',
    'x̸', '≠', '=', '≤', '<=', '≥', '>=', '∫', '∑', '∞',
    'À', 'À', 'ẛ', 'ſ', 's', 'š', 'š', 'Š',
    'a̸', 'ā', 'ā', 'ǖ', 'ǖ', 'ǖ',
    'Ĳa', 'IJa', 'Ŀ', 'L·', 'l·', 'l', 'ŉ', 'ʼn', 'ǰ', 'ǰ',
    'Straße 1', 'Strasse 1', 'Strasse 10', 'Strasse 2', 'abc 1', 'abc 2', 'abc 10', 'abc-1', 'abc-2', 'abc-10',
    '1 abc', '2 abc', '10 abc', '1-abc', '2-abc', '10-abc', '99', '100', '101', '0099', '00100', '1.0', '1.00', '1.5',
    '١٢٣', '123', '１２３', '१२३', 'x١', 'x1', 'x१')

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'testdata', 'text', 'collation-corpus.json')
with open(out, 'w', encoding='ascii') as f:
    json.dump(S, f, indent=0, ensure_ascii=True)
print(len(S), 'strings')
