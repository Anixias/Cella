typedef int i32;
typedef long long i64;

int printf(const char* format, ...);

i32 shared_count = 42;
i64 shared_total = -5000000000;

void bump_shared(void)
{
    shared_count += 1;
    shared_total *= 2;
}

void show_shared(void)
{
    printf("C shared %d %lld\n", shared_count, shared_total);
}
