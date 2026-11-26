"""Publish verified companion files before making their ZIP visible to readers."""
import fcntl
import filecmp
import os
import stat


def publish_archive(directory, archive, companions):
    """Keep existing deliveries intact and allow an interrupted, identical pair to finish."""
    target = directory / archive.name
    descriptor = os.open(directory, os.O_RDONLY)
    created = []
    try:
        fcntl.flock(descriptor, fcntl.LOCK_EX)
        if os.path.lexists(target):
            raise FileExistsError(f"Refuse to overwrite existing delivery: {target.name}")

        missing = []
        for source in companions:
            destination = directory / source.name
            try:
                mode = destination.lstat().st_mode
            except FileNotFoundError:
                missing.append((source, destination))
                continue
            if not stat.S_ISREG(mode) or not filecmp.cmp(source, destination, shallow=False):
                raise FileExistsError(f"Existing delivery companion differs: {destination.name}")

        # link() is exclusive even if another publisher does not participate in the lock.
        # Until the ZIP link succeeds, readers cannot observe an incomplete new delivery.
        try:
            for source, destination in missing:
                created.append((source, destination))
                os.link(source, destination)
            os.link(archive, target)
        except BaseException:
            try:
                published = os.path.samestat(archive.stat(), target.lstat())
            except FileNotFoundError:
                published = False
            if not published:
                for source, destination in reversed(created):
                    try:
                        if os.path.samestat(source.stat(), destination.lstat()):
                            destination.unlink()
                    except FileNotFoundError:
                        pass
            raise
    finally:
        os.close(descriptor)
