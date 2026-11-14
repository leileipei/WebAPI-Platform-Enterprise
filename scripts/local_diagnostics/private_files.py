"""Exclusive UTF-8 files with private permissions from the first open."""
import json
import os


def write_private_text_exclusive(path, text):
    if not isinstance(text, str):
        raise TypeError('PRIVATE_FILE_TEXT_REQUIRED')

    def private_opener(name, flags):
        return os.open(name, flags | getattr(os, 'O_NOFOLLOW', 0), 0o600)

    # Built-in open supports opener; pathlib.Path.open does not.
    # Mode x rejects both existing files and symlinks, including dangling ones.
    with open(path, 'x', encoding='utf-8', opener=private_opener) as out:
        os.fchmod(out.fileno(), 0o600)
        out.write(text)


def write_private_json_exclusive(path, document):
    text = json.dumps(document, ensure_ascii=False, indent=2) + '\n'
    write_private_text_exclusive(path, text)
