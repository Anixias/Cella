import gdb


class StrPrinter:
    def __init__(self, value):
        self.value = value

    def to_string(self):
        length = int(self.value["length"])
        return self.value["data"].lazy_string(encoding="utf-8", length=length)

    def display_hint(self):
        return "string"


def find_printer(value):
    if value.type.strip_typedefs().unqualified().tag == "str":
        return StrPrinter(value)
    return None


gdb.pretty_printers.append(find_printer)
