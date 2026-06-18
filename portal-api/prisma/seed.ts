import * as bcrypt from 'bcryptjs';
import { PrismaClient, UserRole } from '@prisma/client';

const prisma = new PrismaClient();

async function main() {
  const email = 'admin@lugiano.local';
  // Demo password — long enough to defeat dictionary scanners (~70 bits of
  // entropy) while staying typeable in a screen-share. Rotate post-demo.
  const password = 'LugianoDemo2026!';
  const passwordHash = await bcrypt.hash(password, 10);

  await prisma.user.upsert({
    where: { email },
    // Reset the password on every seed run so resets actually take effect.
    // Previously this was {} which made the seeder a no-op on existing users.
    update: { passwordHash },
    create: {
      email,
      passwordHash,
      fullName: 'Dev Admin',
      role: UserRole.ADMIN,
    },
  });

  console.log(`Seeded user: ${email} / ${password}`);
}

main()
  .catch((e) => {
    console.error(e);
    process.exit(1);
  })
  .finally(() => prisma.$disconnect());
