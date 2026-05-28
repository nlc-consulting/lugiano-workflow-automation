-- CreateTable
CREATE TABLE "AppInfo" (
    "id" SERIAL NOT NULL,
    "name" TEXT NOT NULL,
    "createdAt" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT "AppInfo_pkey" PRIMARY KEY ("id")
);
