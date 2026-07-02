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

  // Demo doctor login — Dr. Roger Saias (Center City). DOCTOR role → sees the
  // Doctor View (kicked-back notes) only. Same demo password.
  const drEmail = 'drsaias@papainandrehab.com';
  await prisma.user.upsert({
    where: { email: drEmail },
    update: { passwordHash, role: UserRole.DOCTOR, office: 'Center City', isActive: true },
    create: {
      email: drEmail,
      passwordHash,
      fullName: 'Roger Saias, DC',
      role: UserRole.DOCTOR,
      office: 'Center City',
    },
  });

  console.log(`Seeded user: ${drEmail} / ${password}`);
}

main()
  .catch((e) => {
    console.error(e);
    process.exit(1);
  })
  .finally(() => prisma.$disconnect());
