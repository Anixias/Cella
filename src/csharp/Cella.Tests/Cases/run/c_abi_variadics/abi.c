typedef int i32;
typedef unsigned int u32;
typedef long long i64;
typedef unsigned long long u64;
typedef i32 (*Callback)(i32);

int printf(const char* format, ...);

i64 sum_ints(i32 count, ...)
{
    __builtin_va_list args;
    __builtin_va_start(args, count);
    i64 total = 0;
    for (i32 i = 0; i < count; i++)
        total += __builtin_va_arg(args, i32);

    __builtin_va_end(args);
    return total;
}

double average(i32 count, double first, ...)
{
    __builtin_va_list args;
    __builtin_va_start(args, first);
    double total = first;
    for (i32 i = 1; i < count; i++)
        total += __builtin_va_arg(args, double);

    __builtin_va_end(args);
    return total / count;
}

void show(const char* types, ...)
{
    __builtin_va_list args;
    __builtin_va_start(args, types);
    printf("C show");
    for (const char* type = types; *type; type++)
    {
        switch (*type)
        {
        case 'i': printf(" %d", __builtin_va_arg(args, i32));
            break;
        case 'u': printf(" %u", __builtin_va_arg(args, u32));
            break;
        case 'l': printf(" %lld", __builtin_va_arg(args, i64));
            break;
        case 'L': printf(" %llu", __builtin_va_arg(args, u64));
            break;
        case 'd': printf(" %.3f", __builtin_va_arg(args, double));
            break;
        case 's': printf(" %s", __builtin_va_arg(args, const char *));
            break;
        case 'p': printf(" %d", *__builtin_va_arg(args, i32 *));
            break;
        case 'f':
            {
                Callback callback = __builtin_va_arg(args, Callback);
                printf(" %d", callback(5));
                break;
            }
        default: break;
        }
    }

    __builtin_va_end(args);
    printf("\n");
}
